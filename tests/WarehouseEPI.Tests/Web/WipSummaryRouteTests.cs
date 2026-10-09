using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Reporting;

namespace WarehouseEPI.Tests.Web;

public sealed class WipSummaryRouteTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => WipSummaryTests.Now; }

    [Theory]
    [InlineData("es", "Seleccionado")]
    [InlineData("en", "Selected")]
    public async Task Summary_links_exports_and_paging_share_filters(string language, string selectedText)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(new Clock())));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        await WipSummaryTests.SeedAndVerifyAsync(db);
        var area = await db.Locations.SingleAsync(x => x.Code == "SUMMARY-WIP");
        var user = await db.Users.SingleAsync();
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "0123");
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var forbidden = await client.GetAsync("/Admin/Reports/Wip/Export?format=csv&attention=aged");
        Assert.Equal(HttpStatusCode.Redirect, forbidden.StatusCode);
        Assert.Contains("/Admin/Login", forbidden.Headers.Location!.ToString());
        // Preserve the current server-side report policy; the legacy heading is not authorization.
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Reports/Wip")).StatusCode);
        var login = await client.GetStringAsync("/Admin/Login");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
        }))).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", $"WarehouseEPI.Language={language}");
        foreach (var attention in new[] { "", "pending", "aged" })
        {
            var html = await client.GetStringAsync($"/Reports/Wip?from=2026-09-01&to=2026-10-09&search=SUM-&wipAreaId={area.Id}&attention={attention}");
            var links = Regex.Matches(html, "<a[^>]*data-wip-attention=\"([^\"]+)\"[^>]*>.*?</a>", RegexOptions.Singleline);
            Assert.Equal(2, links.Count);
            var allLink = Regex.Match(html, "<a[^>]*data-wip-all[^>]*>.*?</a>", RegexOptions.Singleline);
            Assert.True(allLink.Success);
            var allQuery = Query(allLink.Value);
            Assert.False(allQuery.TryGetValue("attention", out var allAttention) && !string.IsNullOrEmpty(allAttention));
            Assert.Equal("SUM-", allQuery["search"].ToString());
            Assert.Equal(area.Id.ToString(), allQuery["wipAreaId"].ToString());
            Assert.Equal("1", allQuery["pageNumber"].ToString());
            foreach (Match link in links)
            {
                var key = link.Groups[1].Value;
                var query = Query(link.Value);
                Assert.Equal(key, query["attention"].ToString());
                Assert.Equal("oldest", query["sort"].ToString());
                Assert.Equal("1", query["pageNumber"].ToString());
                Assert.Equal("SUM-", query["search"].ToString());
                Assert.Equal("2026-09-01", query["from"].ToString());
                Assert.Equal("2026-10-09", query["to"].ToString());
                Assert.Equal(area.Id.ToString(), query["wipAreaId"].ToString());
                Assert.Contains(key == "pending" ? "<strong>30</strong>" : "<strong>4</strong>", link.Value);
                Assert.Equal(key == attention, link.Value.Contains("aria-current=\"true\"", StringComparison.Ordinal));
                Assert.Equal(key == attention, WebUtility.HtmlDecode(link.Value).Contains(selectedText, StringComparison.Ordinal));
            }
            foreach (Match anchor in Regex.Matches(html, "<a[^>]*>.*?</a>", RegexOptions.Singleline))
            {
                if (!anchor.Value.Contains("page-link", StringComparison.Ordinal) && !anchor.Value.Contains("/Admin/Reports/Wip/Export", StringComparison.Ordinal)) continue;
                var query = Query(anchor.Value);
                Assert.Equal(attention, query.TryGetValue("attention", out var value) ? value.ToString() : "");
                Assert.Equal(attention == "" ? "newest" : "oldest", query["sort"].ToString());
            }
            var output = Environment.GetEnvironmentVariable("WAREHOUSE_WIP_SUMMARY_FIXTURES");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, $"wip-{language}-{(attention == "" ? "all" : attention)}.html"),
                    Regex.Replace(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", ""));
            }
            var service = scope.ServiceProvider.GetRequiredService<WipReportService>();
            var filter = new WipReportFilter(null, null, "SUM-", area.Id, PendingOnly: attention != "",
                AgedBefore: attention == "aged" ? WipSummaryTests.Now.AddDays(-7) : null, OldestFirst: attention != "");
            var expected = await service.GetTrackedPageAsync(filter, 1, 10_001);
            var url = $"/Admin/Reports/Wip/Export?search=SUM-&wipAreaId={area.Id}&attention={attention}";
            var csv = await client.GetStringAsync(url + "&format=csv");
            var ids = Regex.Matches(csv, "(?m)^\"([a-f0-9-]{36})\"").Select(x => Guid.Parse(x.Groups[1].Value));
            Assert.Equal(expected.Inventory.Select(x => x.DocumentId), ids);
            using var book = new XLWorkbook(new MemoryStream(await client.GetByteArrayAsync(url + "&format=xlsx")));
            var summary = book.Worksheet("Resumen");
            Assert.Equal(expected.Inventory.Count, summary.Cell("B6").GetValue<int>());
            var expectedGroups = expected.Inventory.Where(x => x.Quantity > 0)
                .GroupBy(x => (x.Unit, Product: x.Unit == "UNASSIGNED" ? x.ProductId : (Guid?)null))
                .OrderBy(x => x.Key.Unit, StringComparer.Ordinal).ThenBy(x => x.First().ProductSku, StringComparer.Ordinal).ThenBy(x => x.Key.Product).ToArray();
            for (var group = 0; group < expectedGroups.Length; group++)
            {
                Assert.Equal(expectedGroups[group].Key.Unit, summary.Cell(group + 21, 1).GetString());
                Assert.Equal(expectedGroups[group].Sum(x => x.Quantity), summary.Cell(group + 21, 4).GetValue<decimal>());
            }
            var sheet = book.Worksheet("WIP");
            Assert.Equal(expected.Inventory.Count + 1, sheet.LastRowUsed()!.RowNumber());
            for (var i = 0; i < expected.Inventory.Count; i++)
            {
                Assert.Equal(expected.Inventory[i].DocumentId.ToString(), sheet.Cell(i + 2, 1).GetString());
                Assert.Equal(expected.Inventory[i].Quantity, sheet.Cell(i + 2, 13).GetValue<decimal>());
            }
        }
        var explicitOrder = await client.GetStringAsync($"/Reports/Wip?attention=pending&sort=newest&wipAreaId={area.Id}");
        Assert.Contains("value=\"newest\" selected=\"selected\"", explicitOrder);
        var empty = await client.GetStringAsync($"/Reports/Wip?search=NO-MATCH&wipAreaId={area.Id}");
        Assert.Contains("data-wip-summary-empty", empty);
    }

    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Query(string anchor)
    {
        var href = WebUtility.HtmlDecode(Regex.Match(anchor, "href=\"([^\"]+)\"").Groups[1].Value);
        return QueryHelpers.ParseQuery(new Uri(new Uri("https://localhost"), href).Query);
    }
}
