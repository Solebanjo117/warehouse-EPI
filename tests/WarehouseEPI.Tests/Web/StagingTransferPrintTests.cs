using System.Net;
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
    [InlineData(20)]
    [InlineData(50)]
    public async Task Staging_transfer_prints_destination_plate_instead_of_source_entries(int quantity)
    {
        await using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var preset = PalletLicensePlatePresetCatalog.Initial;
        var template = new LabelTemplate { Code = preset.Code, Kind = LabelTemplateKind.PalletLicensePlate };
        var version = new LabelTemplateVersion { Template = template, Name = preset.Name, Version = 1,
            SizePreset = preset.Size, Status = LabelTemplateStatus.Published, DesignJson = LabelDesignSerializer.Serialize(preset.Design) };
        template.CurrentPublishedVersion = version; template.CurrentPublishedVersionId = version.Id;
        var user = new User { FullName = "Staging print", RoleId = 2, PinLookup = "", PinHash = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(user, "7854");
        var product = new Product { Sku = "STAGING-TRANSFER-PRINT", BaseUnitId = 1 };
        var source = new Location { Code = "STAGING", Kind = LocationKind.Area };
        var destination = new Location { Code = "A-1-1", Kind = LocationKind.Rack };
        db.AddRange(template, version, user, product, source, destination); await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<InventoryMovementService>();
        await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "7854",
            [new(product.Id, 50, DestinationLocationId: source.Id)]));
        await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "7854",
            [new(product.Id, 40, DestinationLocationId: source.Id)]));
        var transfer = await service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Transfer, "7854",
            [new(product.Id, quantity, SourceLocationId: source.Id, DestinationLocationId: destination.Id, AutomaticPalletHandling: true)]));
        Assert.Equal(InventoryMovementStatus.Success, transfer.Status);
        var tracking = scope.ServiceProvider.GetRequiredService<PalletTrackingService>();
        var printable = await tracking.PrintablePlatesForMovementsAsync([transfer.MovementId!.Value]);
        var plate = Assert.Single(printable[transfer.MovementId.Value]);
        Assert.Equal(destination.Id, plate.LocationId);
        Assert.Equal(quantity, plate.Quantity);
        Assert.Equal(destination.Id, (await tracking.SuggestionAsync(transfer.MovementId.Value))!.LocationId);
        var count = await db.PalletPlates.CountAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Operations/PalletLabels?id={plate.Id}&print=true"));
        Assert.Contains("class=\"label-dynamic\"", html);
        Assert.DoesNotContain("id=\"staging-entries\"", html);
        Assert.Contains(plate.Identifier, html);
        var loaded = await scope.ServiceProvider.GetRequiredService<PalletLicensePlateService>().LoadAsync(plate.Id);
        Assert.Equal("A-1-1", loaded.Entry!.Destination);
        Assert.Equal(count, await db.PalletPlates.CountAsync());
        Assert.Equal(90 - quantity, await db.InventoryBalances.Where(b => b.LocationId == source.Id).SumAsync(b => b.Quantity));
    }
}
