using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Web;

public sealed class WipDocumentRouteTests
{
    [Theory]
    [InlineData("es", "WIP · Sin control de existencias", "Pendiente documental")]
    [InlineData("en", "WIP · No inventory tracking", "Documentary outstanding quantity")]
    public async Task Wip_map_detail_print_and_report_render_documentary_contract(string language, string label, string pending)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var wip = new Location
        {
            Code = "M-6-1",
            RowCode = "M",
            RackNumber = 6,
            PalletNumber = 1,
            Kind = LocationKind.Rack,
            OperationalRole = LocationOperationalRole.Wip
        };
        var product = new Product { Sku = "THREAD-TK92-BLACK", BaseUnitId = (await db.Units.FirstAsync()).Id };
        var user = new User { FullName = "WIP fixture", RoleId = (await db.Roles.SingleAsync(x => x.Code == "ADMIN")).Id, PinHash = "", PinLookup = "fixture-wip" };
        await scope.ServiceProvider.GetRequiredService<WarehouseEPI.Infrastructure.Security.UserPinService>().AssignAsync(user, "0137");
        var document = new WipDocument
        {
            Product = product,
            WipLocation = wip,
            ResponsibleUser = user,
            Quantity = 26,
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-90),
            Lots = [new() { Quantity = 26 }]
        };
        db.AddRange(document, new WarehouseMapLayout
        {
            Elements = [new WarehouseMapElement {
            Kind = WarehouseMapElementKind.Rack, RowCode = "M", RackNumber = 6, X = 50, Y = 50, Width = 90, Height = 42 }]
        });
        await db.SaveChangesAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        foreach (var route in new[] { "/Locations", $"/Locations/{wip.Id}", "/Locations/Rack/Print?rowCode=M&rackNumber=6", $"/Reports/Wip/Document?id={document.Id}", "/Reports/Wip" })
        {
            if (route.Contains("/Document"))
            {
                var login = await client.GetStringAsync("/Admin/Login");
                var token = System.Text.RegularExpressions.Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
                using var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Input.Pin"] = "0137",
                    ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value)
                }));
                signedIn.EnsureSuccessStatusCode();
            }
            var html = await client.GetStringAsync(route);
            Assert.True(WebUtility.HtmlDecode(html).Contains(route == "/Reports/Wip" ? pending : label), route + " lacks WIP label: " + System.Text.RegularExpressions.Regex.Match(html, "<h1[^>]*>(.*?)</h1>").Value);
            if (route.Contains("/Document")) Assert.Contains(pending, WebUtility.HtmlDecode(html));
            if (route == "/Reports/Wip")
            {
                Assert.Contains(product.Sku, html); // Late capture must remain discoverable by default.
                var from = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
                var filtered = await client.GetStringAsync($"/Reports/Wip?from={from}");
                Assert.DoesNotContain(product.Sku, filtered);
            }
            var root = FindRoot();
            var directory = Path.Combine(root, "artifacts", "ui", "wip-document");
            Directory.CreateDirectory(directory);
            var name = route == "/Reports/Wip" ? "report" : route.Contains("/Document") ? "document" : route.Contains("/Print") ? "print" : route == "/Locations" ? "map" : "detail";
            await File.WriteAllTextAsync(Path.Combine(directory, $"{name}-{language}.html"), html);
        }
    }

    private static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !Directory.Exists(Path.Combine(path.FullName, "src"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
