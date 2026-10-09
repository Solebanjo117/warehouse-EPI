using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Web;

public sealed partial class PalletTrackingRouteTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("999")]
    public async Task Staging_prints_selected_entries_and_recovers_historical_entry_through_antiforgery_post(string? postedQuantity)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var preset = PalletLicensePlatePresetCatalog.Initial;
        var template = new LabelTemplate { Code = preset.Code, Kind = LabelTemplateKind.PalletLicensePlate };
        var version = new LabelTemplateVersion { Template = template, Name = preset.Name, Version = 1,
            SizePreset = preset.Size, Status = LabelTemplateStatus.Published,
            DesignJson = LabelDesignSerializer.Serialize(preset.Design) };
        template.CurrentPublishedVersion = version;
        template.CurrentPublishedVersionId = version.Id;
        db.AddRange(template, version);
        var pins = scope.ServiceProvider.GetRequiredService<UserPinService>();
        var user = new User { FullName = "Staging web", RoleId = 2, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(user, "7854");
        var product = new Product { Sku = "STAGING-WEB", BaseUnitId = 1 };
        var location = new Location { Code = "BEFORE-STAGING", Kind = LocationKind.Area };
        db.AddRange(user, product, location); await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<InventoryMovementService>();
        var legacy = await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "7854",
            [new(product.Id, 40, DestinationLocationId: location.Id)]));
        Assert.Equal(InventoryMovementStatus.Success, legacy.Status);
        location.Code = "STAGING"; await db.SaveChangesAsync();
        var plateIds = new List<Guid>();
        foreach (var amount in new[] { 100m, 150m })
        {
            var entry = await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "7854",
                [new(product.Id, amount, DestinationLocationId: location.Id)]));
            plateIds.Add(Assert.Single(entry.Plates!).PlateId);
        }
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/PalletLabels?location=STAGING&productId={product.Id}"));
        Assert.Contains("Imprimir por entrada", html);
        Assert.DoesNotContain("name=\"Staging.Quantity\"", html);
        Assert.DoesNotContain("handler=PreparePrint", html);
        Assert.Contains("handler=StagingEntry", html);
        var selectedMovement = (await db.PalletPlates.SingleAsync(x => x.Id == plateIds[0])).OriginMovementId;
        var direct = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/PalletLabels?handler=StagingEntry&movementId={selectedMovement}"));
        Assert.Equal(1, Regex.Count(direct, "class=\"label-dynamic\""));
        Assert.Contains($"PLT-{plateIds[0]:N}".ToUpperInvariant(), direct);
        Assert.DoesNotContain($"PLT-{plateIds[1]:N}".ToUpperInvariant(), direct);
        Assert.DoesNotContain("id=\"staging-entries\"", direct);
        var historical = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/PalletLabels?handler=StagingEntry&movementId={legacy.MovementId}"));
        Assert.Equal(1, Regex.Count(historical, "name=\"Staging.MovementId\""));
        Assert.Contains($"value=\"{legacy.MovementId}\"", historical);
        Assert.DoesNotContain("class=\"label-dynamic\"", historical);
        var invalid = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/PalletLabels?handler=StagingEntry&movementId={Guid.NewGuid()}"));
        Assert.Contains("La entrada no está vigente o no pertenece a STAGING.", invalid);
        Assert.DoesNotContain("class=\"label-dynamic\"", invalid);
        var payload = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", Token(html)), new("Staging.LocationId", location.Id.ToString())
        };
        payload.AddRange(plateIds.Select(id => new KeyValuePair<string, string>("Staging.PlateIds", id.ToString())));
        var response = await client.PostAsync("/Operations/PalletLabels?handler=PrintStagingSelection", new FormUrlEncodedContent(payload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.True(Regex.Count(html, "class=\"label-dynamic\"") == 2,
            string.Join(" ", Regex.Matches(html, "<li>(.*?)</li>").Select(x => x.Groups[1].Value)));
        Assert.All(plateIds, id => Assert.Contains($"PLT-{id:N}".ToUpperInvariant(), html));
        var identification = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html), ["Staging.LocationId"] = location.Id.ToString(),
            ["Staging.MovementId"] = legacy.MovementId!.Value.ToString(),
            ["Staging.OperationId"] = Guid.NewGuid().ToString()
        };
        if (postedQuantity is not null) identification["Staging.Quantity"] = postedQuantity;
        response = await client.PostAsync("/Operations/PalletLabels?handler=IdentifyStagingEntry", new FormUrlEncodedContent(identification));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("label-preview", html);
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.PalletPlates.CountAsync(x => x.ProductId == product.Id));
        Assert.Equal(40, (await db.PalletPlates.SingleAsync(x => x.OriginMovementId == legacy.MovementId)).Quantity);
        Assert.Equal(290, await db.InventoryBalances.Where(x => x.ProductId == product.Id).SumAsync(x => x.Quantity));
    }
}
