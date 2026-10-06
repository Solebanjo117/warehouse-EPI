using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Tests.Imports;
using WarehouseEPI.Tests.Locations;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Tests.Web;

public sealed class RackWipAssociationRouteTests
{
    [Fact]
    public async Task Map_summary_shares_latest_five_documents_and_keeps_position_history_separate()
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var (user, area, other, position, product) = await RackWipAssociationTests.SeedAsync(db);
        db.LocationRackWipAssociations.AddRange(
            new() { RowCode = "M", RackNumber = 5, WipAreaId = area.Id },
            new() { RowCode = "M", RackNumber = 6, WipAreaId = area.Id });
        db.WarehouseMapLayouts.Add(new WarehouseMapLayout
        {
            Elements = [
            new() { Kind = WarehouseMapElementKind.Rack, RowCode = "M", RackNumber = 5, X = 50, Y = 50, Width = 90, Height = 42 },
            new() { Kind = WarehouseMapElementKind.Rack, RowCode = "M", RackNumber = 6, X = 150, Y = 50, Width = 90, Height = 42 }]
        });
        var date = new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero);
        WipDocument Delivery(Location destination, int minutes, bool cancelled = false) => new()
        { WipLocation = destination, Product = product, ResponsibleUser = user, OccurredAt = date.AddMinutes(minutes), Quantity = 100, IsCancelled = cancelled };
        var deliveries = Enumerable.Range(0, 6).Select(i => Delivery(area, i)).ToArray();
        var direct = Delivery(position, 10);
        var elsewhere = Delivery(other, 11);
        var cancelled = Delivery(area, 12, true);
        db.WipDocuments.AddRange(deliveries);
        db.WipDocuments.AddRange(direct, elsewhere, cancelled);
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost") });
        Assert.DoesNotContain("data-associated-wip-summary", await client.GetStringAsync("/Locations"));
        await scope.ServiceProvider.GetRequiredService<WarehouseEPI.Infrastructure.Security.UserPinService>().AssignAsync(user, "1234");
        await db.SaveChangesAsync();
        await LoginAsync(client);
        var html = await client.GetStringAsync("/Locations");
        var summaries = Regex.Matches(html, "<details[^>]*data-associated-wip-summary=[\\s\\S]*?</details>");
        Assert.Equal(2, summaries.Count);
        foreach (Match summary in summaries)
        {
            var content = summary.Value;
            Assert.Equal(5, Regex.Count(content, "class=\"map-wip-issue\""));
            foreach (var delivery in deliveries.Skip(1)) Assert.Contains(delivery.Id.ToString(), content);
            foreach (var excluded in new[] { deliveries[0], direct, elsewhere, cancelled }) Assert.DoesNotContain(excluded.Id.ToString(), content);
            Assert.True(content.IndexOf(deliveries[5].Id.ToString(), StringComparison.Ordinal) < content.IndexOf(deliveries[1].Id.ToString(), StringComparison.Ordinal));
        }
        var association = await db.LocationRackWipAssociations.SingleAsync(x => x.RackNumber == 5);
        association.WipAreaId = other.Id;
        await db.SaveChangesAsync();
        html = await client.GetStringAsync("/Locations");
        Assert.Contains(elsewhere.Id.ToString(), html);
        Assert.Contains(deliveries[5].Id.ToString(), html);
        Assert.Equal(9, await db.WipDocuments.CountAsync());
    }

    [Theory]
    [InlineData("es", "Posiciones WIP asociadas a WIP-2", "WIP asociado")]
    [InlineData("en", "WIP positions associated with WIP-2", "Associated WIP")]
    public async Task Linked_rack_surfaces_render_shared_report_and_preserve_original_position_link(string language, string label, string editorLabel)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var (user, area, _, position, product) = await RackWipAssociationTests.SeedAsync(db);
        var racks = new LocationRackAdministrationService(db, WipTransferImportTests.Pins(db), TimeProvider.System);
        foreach (var rack in new short[] { 5, 6 })
            Assert.Equal(LocationRackSaveStatus.Success, (await racks.SaveAsync(RackWipAssociationTests.Command(user.Id, area.Id, rack))).Status);
        db.WipDocuments.Add(new WipDocument { WipLocation = area, Product = product, ResponsibleUser = user, OccurredAt = new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero), Quantity = 100 });
        db.WarehouseMapLayouts.Add(new WarehouseMapLayout
        {
            Elements = [new WarehouseMapElement
        { Kind = WarehouseMapElementKind.Rack, RowCode = "M", RackNumber = 5, X = 50, Y = 50, Width = 90, Height = 42 }]
        });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<WarehouseEPI.Infrastructure.Security.UserPinService>().AssignAsync(user, "1234");
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("Cookie", $"{UiLanguage.CookieName}={language}");
        foreach (var (name, route) in new[] { ("map", "/Locations"), ("racks", "/Locations?viewMode=racks"),
            ("table", "/Locations?viewMode=table"), ("detail", $"/Locations/{position.Id}"),
            ("print", "/Locations/Rack/Print?rowCode=M&rackNumber=5") })
        {
            var html = await client.GetStringAsync(route);
            Assert.Contains(label, WebUtility.HtmlDecode(html));
            Assert.Contains($"wipAreaId={area.Id}", html);
            if (name == "detail") Assert.Contains($"wipAreaId={position.Id}", html);
            await SaveFixtureAsync($"{name}-{language}", html);
        }
        var login = await client.GetStringAsync("/Admin/Login");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        using var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = "1234", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value) }));
        signedIn.EnsureSuccessStatusCode();
        var loginResult = await signedIn.Content.ReadAsStringAsync();
        Assert.DoesNotContain("name=\"Input.Pin\"", loginResult);
        var map = await client.GetStringAsync("/Locations");
        Assert.Contains(language == "es" ? "Últimas entregas a WIP-2" : "Latest deliveries to WIP-2", WebUtility.HtmlDecode(map));
        Assert.Contains("data-associated-wip-summary", map);
        Assert.Contains(product.Sku, map);
        await SaveFixtureAsync($"map-{language}", map);
        var adminMap = await client.GetStringAsync("/Admin/Catalogs/Locations");
        Assert.Contains(language == "es" ? "Últimas entregas a WIP-2" : "Latest deliveries to WIP-2", WebUtility.HtmlDecode(adminMap));
        Assert.Contains("data-associated-wip-summary", adminMap);
        Assert.Contains(product.Sku, adminMap);
        var report = await client.GetStringAsync($"/Reports/Wip?wipAreaId={area.Id}");
        await SaveFixtureAsync($"report-{language}", report);
        Assert.Contains("M-5, M-6", WebUtility.HtmlDecode(report));
        Assert.Contains(product.Sku, report);
        await SaveFixtureAsync($"report-{language}", report);
        var editor = await client.GetStringAsync("/Admin/Catalogs/Locations/Rack/Edit?rowCode=M&rackNumber=5");
        Assert.Contains(editorLabel, WebUtility.HtmlDecode(editor));
        Assert.Contains("name=\"Input.WipAreaId\"", editor);
        Assert.Contains("name=\"Input.ExpectedWipAreaId\"", editor);
        await SaveFixtureAsync($"editor-{language}", editor);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/Admin/Login");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        using var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Pin"] = "1234", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value) }));
        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain("name=\"Input.Pin\"", await response.Content.ReadAsStringAsync());
    }

    private static async Task SaveFixtureAsync(string name, string html)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WarehouseEPI.sln"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "artifacts", "ui", "rack-wip");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, name + ".html"), html);
    }
}
