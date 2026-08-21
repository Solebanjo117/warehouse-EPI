using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Reporting;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class InventoryAnalyticsPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Analytics_grouping_and_effective_exit_queries_translate_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-AN-{suffix}", $"PGA-{suffix}", "4287");

        await using var db = fixture.CreateDbContext();
        var product = await db.Products.SingleAsync(candidate => candidate.Id == seed.ProductId);
        var user = await db.Users.SingleAsync(candidate => candidate.FullName == $"Operador PG-AN-{suffix}");
        const short rackNumber = 32000;
        var rack = new Location
        {
            Code = $"Z-{rackNumber}-1",
            Kind = LocationKind.Rack,
            OperationalRole = LocationOperationalRole.Storage,
            RowCode = "Z",
            RackNumber = rackNumber,
            PalletNumber = 1
        };
        db.Add(rack);
        db.ProductBarcodes.Add(new ProductBarcode { Product = product, Barcode = $"ANALYTICS-{suffix}-BARCODE" });
        db.InventoryBalances.Add(
            new InventoryBalance { Product = product, Location = rack, Quantity = 3m });
        var exit = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = InventoryMovementType.Exit,
            ResponsibleUser = user,
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-31)
        };
        exit.Lines.Add(new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            Quantity = 2m,
            LineNumber = 1
        });
        db.Add(exit);
        await db.SaveChangesAsync();

        var service = new InventoryAnalyticsService(db, new WarehouseSettingsService(db));
        var occupancy = await service.GetOccupancyAsync();
        var activity = await service.GetExitActivityPageAsync(new InventoryAnalyticsFilter(
            ProductStatus: "all",
            Search: $"{suffix}-barcode",
            PageSize: 1));
        var stagnant = await service.GetStagnantPageAsync(
            new InventoryAnalyticsFilter(ProductStatus: "all"),
            DateTimeOffset.UtcNow);

        Assert.Contains(occupancy.Rows, row => row.RowCode == "Z" && row.Summary.OccupiedCount == 1);
        Assert.Contains(activity.Items, row => row.ProductId == product.Id && row.EffectiveExitMovementCount == 1);
        Assert.Equal(1, activity.TotalCount);
        Assert.Equal(1, activity.PageSize);
        Assert.Contains(stagnant.Items, row => row.ProductId == product.Id);
    }
}
