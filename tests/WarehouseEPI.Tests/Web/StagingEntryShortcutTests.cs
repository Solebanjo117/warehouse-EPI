using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Navigation;

namespace WarehouseEPI.Tests.Web;

public sealed class StagingEntryShortcutTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Shortcut_prefills_editable_entry_and_preserves_normal_links(string language)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{WarehouseEPI.Web.Localization.UiLanguage.CookieName}={language}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var staging = new Location { Code = "STAGING", Kind = LocationKind.Area };
        var other = new Location { Code = "OTHER", Kind = LocationKind.Area };
        db.AddRange(staging, other);
        await db.SaveChangesAsync();
        var before = db.InventoryMovements.Count();
        var hub = await client.GetStringAsync("/Modules/operations");
        Assert.Contains("href=\"/Operations/Entry?mode=staging\"", hub);
        Assert.Contains(language == "en" ? "Receive in STAGING" : "Recibir en STAGING", WebUtility.HtmlDecode(hub));
        var html = await client.GetStringAsync("/Operations/Entry?mode=staging");
        Assert.Contains("data-staging-count=\"0\"", hub);
        Assert.Contains("data-staging-count=\"0\"", html);
        Assert.Contains("href=\"/Operations/Staging\"", html);
        Assert.DoesNotContain("data-staging-count", await client.GetStringAsync("/Operations/Entry"));
        Assert.Equal(staging.Id.ToString(), Input(html, "Input.DestinationLocationId"));
        Assert.Equal("", Input(html, "Input.ProductId"));
        Assert.DoesNotMatch("id=\"destination-code\"[^>]*readonly", html);
        Assert.Equal("", Input(await client.GetStringAsync("/Operations/Entry"), "Input.DestinationLocationId"));
        Assert.Equal(other.Id.ToString(), Input(await client.GetStringAsync($"/Operations/Entry?destinationLocationId={other.Id}"), "Input.DestinationLocationId"));
        Assert.Equal(before, db.InventoryMovements.Count());

        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_STAGING_SHORTCUT_FIXTURES");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"entry-{language}.html"), html);
            await File.WriteAllTextAsync(Path.Combine(directory, $"hub-{language}.html"), hub);
        }
    }

    [Theory]
    [InlineData("missing", "es")]
    [InlineData("inactive", "es")]
    [InlineData("blocked", "es")]
    [InlineData("absent", "es")]
    [InlineData("wip", "es")]
    [InlineData("missing", "en")]
    public async Task Unavailable_staging_leaves_destination_empty_with_localized_warning(string state, string language)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{WarehouseEPI.Web.Localization.UiLanguage.CookieName}={language}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        if (state != "missing")
        {
            db.Locations.Add(new Location { Code = "STAGING", Kind = LocationKind.Area,
                IsActive = state != "inactive", IsBlocked = state == "blocked", IsPhysicallyPresent = state != "absent",
                OperationalRole = state == "wip" ? LocationOperationalRole.Wip : LocationOperationalRole.Storage });
            await db.SaveChangesAsync();
        }
        var html = await client.GetStringAsync("/Operations/Entry?mode=staging");
        Assert.Equal("", Input(html, "Input.DestinationLocationId"));
        Assert.Contains(language == "en" ? "STAGING is unavailable. Select another destination location" :
            "STAGING no está disponible. Selecciona otra ubicación destino", WebUtility.HtmlDecode(html));
    }

    [Theory]
    [InlineData(1, "es")]
    [InlineData(3, "es")]
    [InlineData(1, "en")]
    [InlineData(3, "en")]
    public async Task Counter_is_shared_and_failed_post_keeps_mode_and_chosen_destination(int count, string language)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{WarehouseEPI.Web.Localization.UiLanguage.CookieName}={language}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var staging = new Location { Code = "STAGING", Kind = LocationKind.Area };
        var other = new Location { Code = "OTHER", Kind = LocationKind.Area };
        var product = new Product { Sku = "COUNT", BaseUnitId = 1 };
        db.AddRange(staging, other, product);
        for (var i = 0; i < count; i++)
            db.InventoryMovements.Add(new InventoryMovement { OperationId = Guid.NewGuid(), RequestFingerprint = $"history-{i}",
                Type = InventoryMovementType.Entry, Lines = [new InventoryMovementLine { Product = product, UnitId = 1,
                    DestinationLocation = staging, Quantity = 10, LineNumber = 1 }] });
        await db.SaveChangesAsync();
        var html = await client.GetStringAsync("/Operations/Entry?mode=staging");
        Assert.Contains($"data-staging-count=\"{count}\"", html);
        Assert.Contains($"data-staging-count=\"{count}\"", await client.GetStringAsync("/Modules/operations"));
        Assert.DoesNotContain("data-staging-count", await client.GetStringAsync("/Modules/production"));
        var label = language == "es" ? count == 1 ? "1 pendiente" : "3 pendientes" : $"{count} pending";
        Assert.Contains(label, WebUtility.HtmlDecode(html));
        Assert.Equal("staging", Input(html, "Mode"));
        // Invalid quantity: render the form again without applying the GET destination default.
        var response = await client.PostAsync("/Operations/Entry", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["Mode"] = "staging", ["Input.OperationId"] = Guid.NewGuid().ToString(), ["Input.ProductId"] = product.Id.ToString(),
            ["Input.DestinationLocationId"] = other.Id.ToString(), ["Input.Quantity"] = "0", ["Input.Pin"] = "1234",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Input(html, "__RequestVerificationToken"))
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var failed = await response.Content.ReadAsStringAsync();
        Assert.Contains($"data-staging-count=\"{count}\"", failed);
        Assert.Equal("staging", Input(failed, "Mode"));
        Assert.Equal(other.Id.ToString(), Input(failed, "Input.DestinationLocationId"));
        Assert.Equal(count, db.InventoryMovements.Count());
    }

    [Fact]
    public void Shortcut_follows_entry_and_route_values_preserve_view()
    {
        var actions = ModuleNavigation.GetVisible((string?)null).Single(m => m.Key == "operations").Sections[0].Actions;
        Assert.Equal("/Operations/Entry", actions[0].Page);
        Assert.Null(actions[0].Mode);
        Assert.Equal("staging", actions[1].RouteValues["mode"]);
        var combined = new ModuleAction("test", "test", "entry", "/Operations/Entry", View: "pending", Mode: "staging");
        Assert.Equal("pending", combined.RouteValues["view"]);
        Assert.Equal("staging", combined.RouteValues["mode"]);
    }

    private static string Input(string html, string name)
    {
        var tag = Regex.Match(html, $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*>");
        Assert.True(tag.Success, $"Missing input {name}");
        return Regex.Match(tag.Value, "value=\"([^\"]*)\"").Groups[1].Value;
    }
}
