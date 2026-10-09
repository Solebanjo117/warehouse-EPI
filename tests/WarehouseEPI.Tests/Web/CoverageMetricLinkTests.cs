using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class CoverageMetricLinkTests
{
    [Theory]
    [InlineData("es", "Seleccionado")]
    [InlineData("en", "Selected")]
    public async Task Rendered_links_preserve_applied_filters_and_select_empty_categories(string language, string selectedText)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var admin = new User { FullName = "Coverage test", RoleId = 1, PinHash = "", PinLookup = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(admin, "0123");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/Admin/Login");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "0123", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
        }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", $"WarehouseEPI.Language={language}");
        var keys = new[] { "critical", "low", "normal", "excess", "norecentconsumption", "exhausted" };
        foreach (var selected in keys.Append(""))
        {
            var html = await client.GetStringAsync($"/Reports/Inventory?view=coverage&period=60&status=all&search=A%26B&unitId=1&pageNumber=9&coverageClass={selected}");
            var links = Regex.Matches(html, "<a[^>]*data-coverage-class=\"([^\"]+)\"[^>]*>.*?</a>", RegexOptions.Singleline);
            Assert.Equal(6, links.Count);
            foreach (Match link in links)
            {
                var key = link.Groups[1].Value;
                Assert.Contains(key, keys);
                VerifyLink(link.Value, key);
                Assert.Equal(key == selected, link.Value.Contains("aria-current=\"true\"", StringComparison.Ordinal));
                Assert.Equal(key == selected, WebUtility.HtmlDecode(link.Value).Contains(selectedText, StringComparison.Ordinal));
                Assert.Contains("<strong>0</strong>", link.Value);
            }
            var all = Regex.Match(html, "<a[^>]*data-coverage-all[^>]*>.*?</a>", RegexOptions.Singleline);
            Assert.True(all.Success);
            VerifyLink(all.Value, "");
            Assert.Equal(selected == "", all.Value.Contains("aria-current=\"true\"", StringComparison.Ordinal));
            if (selected != "")
                Assert.Matches($"<option value=\"{selected}\" selected=\"selected\">", html);
            var output = Environment.GetEnvironmentVariable("WAREHOUSE_COVERAGE_FIXTURES");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, $"coverage-{language}-{(selected == "" ? "all" : selected)}.html"),
                    Regex.Replace(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", ""));
            }
        }
    }

    private static void VerifyLink(string html, string classification)
    {
        var href = WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]+)\"").Groups[1].Value);
        var url = new Uri(new Uri("https://localhost"), href);
        Assert.Equal("/Reports/Inventory", url.AbsolutePath);
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("coverage", query["view"].ToString());
        Assert.Equal("60", query["period"].ToString());
        Assert.Equal("all", query["status"].ToString());
        Assert.Equal("A&B", query["search"].ToString());
        Assert.Equal("1", query["unitId"].ToString());
        Assert.Equal("1", query["pageNumber"].ToString());
        Assert.Equal(classification, query.TryGetValue("coverageClass", out var value) ? value.ToString() : "");
    }
}
