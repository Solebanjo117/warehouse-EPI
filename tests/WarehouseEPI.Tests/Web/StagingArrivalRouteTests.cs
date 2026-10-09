using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed class StagingArrivalRouteTests
{
    private sealed class StagingClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Priority_filters_summary_identification_errors_and_focused_arrivals(string language)
    {
        var now = new DateTimeOffset(2026, 10, 8, 18, 0, 0, TimeSpan.Zero);
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(new StagingClock(now))));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{WarehouseEPI.Web.Localization.UiLanguage.CookieName}={language}");
        var empty = await client.GetStringAsync("/Operations/Staging");
        Assert.Contains("data-pending-total>0</dd>", empty);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var user = new User { FullName = "Age operator", RoleId = 2, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "7854");
        var product = new Product { Sku = "AGE-SKU", BaseUnitId = 1 };
        var staging = new Location { Code = "STAGING", Kind = LocationKind.Area };
        var destination = new Location { Code = "AGE-DEST", Kind = LocationKind.Area };
        db.AddRange(user, product, staging, destination); await db.SaveChangesAsync();
        for (var i = 0; i < 28; i++)
            db.InventoryMovements.Add(new() { OperationId = Guid.NewGuid(), RequestFingerprint = $"age-{i}", Type = InventoryMovementType.Entry,
                ResponsibleUserId = user.Id, Reference = $"AGE-{i}", OccurredAt = now.AddHours(-48 - i),
                Lines = [new() { ProductId = product.Id, UnitId = 1, Quantity = 1, DestinationLocationId = staging.Id, LineNumber = 1 }] });
        await db.SaveChangesAsync();
        var html = await client.GetStringAsync("/Operations/Staging?search=AGE&priority=urgent&pageNumber=2");
        Assert.Equal(3, Regex.Count(html, "data-arrival-priority=\"urgent\""));
        Assert.Contains("data-pending-total>28</dd>", html);
        Assert.Contains("data-bs-toggle=\"collapse\"", html);
        Assert.DoesNotContain("/Operations/Staging/Split", html);
        Assert.Contains("priority=urgent", html);
        Assert.Contains(language == "en" ? "Global pending summary" : "Resumen global de pendientes", WebUtility.HtmlDecode(html));
        Assert.Contains("data-pending-total>28</dd>", await client.GetStringAsync("/Operations/Staging?search=absent&priority=normal"));
        Assert.Equal(25, Regex.Count(await client.GetStringAsync("/Operations/Staging?priority=invalid"), "data-arrival-priority="));
        var query = new StagingArrivalQuery(db);
        var line = (await query.ListAsync(null)).Items[0];
        var focused = await client.GetStringAsync($"/Operations/Staging?arrivalLineId={line.LineId}&search=absent&priority=normal&pageNumber=9");
        Assert.Single(Regex.Matches(focused, "data-arrival-priority="));
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var failed = await client.PostAsync("/Operations/Staging?handler=Identify", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["__RequestVerificationToken"] = token, ["Arrival"] = line.LineId.ToString(), ["OperationId"] = Guid.NewGuid().ToString(),
            ["PhysicalQuantity"] = "0", ["Search"] = "AGE", ["Priority"] = "urgent", ["PageNumber"] = "2"
        }));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var failedHtml = await failed.Content.ReadAsStringAsync();
        Assert.Equal(3, Regex.Count(failedHtml, "data-arrival-priority=\"urgent\""));
        Assert.Contains("priority=urgent", failedHtml);
        var service = scope.ServiceProvider.GetRequiredService<InventoryMovementService>();
        var entry = await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "7854", [new(product.Id, 5, DestinationLocationId: staging.Id)]));
        var current = Assert.Single(await query.ForMovementAsync(entry.MovementId!.Value));
        var localTime = await scope.ServiceProvider.GetRequiredService<WarehouseEPI.Infrastructure.Settings.WarehouseClock>().ConvertAsync(current.OccurredAt);
        var currentHtml = await client.GetStringAsync($"/Operations/Staging?arrivalLineId={current.LineId}");
        Assert.Contains(localTime.ToString("dd/MM/yyyy HH:mm"), currentHtml);
        Assert.Contains("data-arrival-priority=\"normal\"", currentHtml);
        Assert.Contains("/Operations/Staging/Split", currentHtml);
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmStagingAsync(new(Guid.NewGuid(), current.LineId, destination.Id, current.Version, "7854"))).Status);
        var completed = await client.GetStringAsync($"/Operations/Staging?arrivalLineId={current.LineId}");
        Assert.DoesNotContain("data-arrival-priority=", completed);
        Assert.DoesNotContain("/Operations/Staging/Split", completed);
        Assert.DoesNotContain("/Operations/Staging/Putaway", completed);
        await ExportAsync($"staging-priority-{language}.html", html);
        await ExportAsync($"staging-priority-error-{language}.html", failedHtml);
        await ExportAsync($"staging-priority-empty-{language}.html", empty);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task Staging_page_putaway_uses_server_identity_and_preserves_antiforgery(string language)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{WarehouseEPI.Web.Localization.UiLanguage.CookieName}={language}");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var user = new User { FullName = "Staging operator", RoleId = 2, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "7854");
        var product = new Product { Sku = "STAGING-ARRIVAL", Description = "Material de prueba para acomodo", BaseUnitId = 1 };
        var staging = new Location { Code = "STAGING", Kind = LocationKind.Area };
        var destination = new Location { Code = "DEST-STAGING", Kind = LocationKind.Area };
        db.AddRange(user, product, staging, destination); await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<InventoryMovementService>();
        for (var i = 1; i <= 2; i++)
            Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry,
                "7854", [new(product.Id, i * 50, DestinationLocationId: staging.Id)], Reference: $"ARRIVAL-{i}"))).Status);
        var arrival = (await new StagingArrivalQuery(db).ListAsync(null)).Items.Single(r => r.Received == 100);
        var listHtml = await client.GetStringAsync("/Operations/Staging");
        Assert.Contains(language == "en" ? "Staging list" : "Lista de staging", listHtml);
        Assert.Contains("ARRIVAL-1", listHtml); Assert.Contains("ARRIVAL-2", listHtml);
        Assert.Equal(2, Regex.Count(listHtml, "data-bs-toggle=\"dropdown\""));
        Assert.DoesNotContain("data-open-incidents", listHtml);
        var url = $"/Operations/Staging/Putaway?arrival={arrival.LineId}";
        var emptyProposals = await client.GetStringAsync(url);
        Assert.Contains("data-destination-proposals", emptyProposals);
        Assert.DoesNotContain("data-destination-proposal\n", emptyProposals);
        Assert.DoesNotContain("data-location-id=", emptyProposals);
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry,
            "7854", [new(product.Id, 30, DestinationLocationId: destination.Id)]))).Status);
        var secondary = new Location { Code = "A-PROPOSAL" };
        var blocked = new Location { Code = "BLOCKED-PROPOSAL", IsBlocked = true };
        var inactive = new Location { Code = "INACTIVE-PROPOSAL", IsActive = false };
        var missing = new Location { Code = "MISSING-PROPOSAL", IsPhysicallyPresent = false };
        var wip = new Location { Code = "WIP-PROPOSAL", OperationalRole = LocationOperationalRole.Wip };
        var negative = new Location { Code = "NEGATIVE-PROPOSAL" };
        var lot = new ProductLot { ProductId = product.Id, Number = "PROPOSAL-LOT", NormalizedNumber = "PROPOSAL-LOT" };
        var lotBalance = new InventoryBalance { ProductId = product.Id, LocationId = secondary.Id, LotId = lot.Id, Quantity = 5 };
        db.AddRange(secondary, blocked, inactive, missing, wip, negative, lot, lotBalance);
        foreach (var location in new[] { secondary, blocked, inactive, missing, wip, negative })
            db.InventoryBalances.Add(new() { ProductId = product.Id, LocationId = location.Id, Quantity = location == negative ? -10 : 10 });
        await db.SaveChangesAsync();
        var multiple = await client.GetStringAsync(url);
        Assert.Equal(2, Regex.Count(multiple, "data-location-id="));
        Assert.True(multiple.IndexOf("data-location-code=\"A-PROPOSAL\"", StringComparison.Ordinal) < multiple.IndexOf("data-location-code=\"DEST-STAGING\"", StringComparison.Ordinal));
        Assert.DoesNotContain($"data-location-id=\"{staging.Id}\"", multiple);
        lotBalance.Quantity = -10;
        await db.SaveChangesAsync();
        var rawHtml = await client.GetStringAsync(url);
        var html = WebUtility.HtmlDecode(rawHtml);
        Assert.Contains("data-fixed-transfer=\"true\"", html);
        Assert.Matches("id=\"source-code\"[^>]*readonly", html);
        Assert.Single(Regex.Matches(html, "data-location-id="));
        Assert.Contains($"data-location-id=\"{destination.Id}\"", html);
        Assert.DoesNotMatch("id=\"destination-code\"[^>]*value=\"[^\"]+\"", html);
        await ExportAsync($"staging-list-{language}.html", listHtml);
        await ExportAsync($"staging-putaway-{language}.html", rawHtml);
        await ExportAsync($"staging-lookup-{language}.json", System.Text.Json.JsonSerializer.Serialize(new {
            product = new { id = product.Id, sku = product.Sku, description = product.Description, unitCode = "PZA", allowsDecimals = false },
            location = new { id = destination.Id, code = destination.Code, description = "Destino de prueba", isWip = false }
        }));
        var payload = new Dictionary<string, string> {
            ["Arrival"] = arrival.LineId.ToString(), ["Version"] = arrival.Version,
            ["Input.OperationId"] = Guid.NewGuid().ToString(), ["Input.DestinationLocationId"] = destination.Id.ToString(),
            ["Input.Pin"] = "7854", ["Input.ProductId"] = Guid.NewGuid().ToString(),
            ["Input.SourceLocationId"] = destination.Id.ToString(), ["Input.Quantity"] = "1"
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, new FormUrlEncodedContent(payload))).StatusCode);
        payload["__RequestVerificationToken"] = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        payload["Input.Pin"] = "0000";
        var invalidPin = await client.PostAsync(url, new FormUrlEncodedContent(payload));
        Assert.Equal(HttpStatusCode.OK, invalidPin.StatusCode);
        var recovered = await invalidPin.Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{destination.Id}\"", recovered);
        Assert.Contains($"data-location-id=\"{destination.Id}\"", recovered);
        Assert.DoesNotContain("value=\"0000\"", recovered);
        payload["Input.Pin"] = "7854";
        var result = await client.PostAsync(url, new FormUrlEncodedContent(payload));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.StartsWith("/Operations/Staging?receipt=", result.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(url, new FormUrlEncodedContent(payload))).StatusCode);
        var pending = Assert.Single((await new StagingArrivalQuery(db).ListAsync(null)).Items);
        Assert.Equal(50, pending.Pending);
    }

    private static async Task ExportAsync(string name, string content)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WarehouseEPI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, "artifacts", "staging-ui", "fixtures");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name), content);
    }
}
