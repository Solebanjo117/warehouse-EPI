using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Reporting;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class InventoryAnalyticsPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Heatmap_distinct_movement_and_product_location_grouping_translate_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-HM-{suffix}", $"PGH-{suffix}", "4399");

        await using var db = fixture.CreateDbContext();
        var product = await db.Products.SingleAsync(candidate => candidate.Id == seed.ProductId);
        var user = await db.Users.SingleAsync(candidate => candidate.FullName == $"Operador PG-HM-{suffix}");
        const string rowCode = "H";
        const short rackNumber = 31000;
        var firstPosition = new Location
        {
            Code = $"{rowCode}-{rackNumber}-1",
            Kind = LocationKind.Rack,
            OperationalRole = LocationOperationalRole.Storage,
            RowCode = rowCode,
            RackNumber = rackNumber,
            PalletNumber = 1
        };
        var secondPosition = new Location
        {
            Code = $"{rowCode}-{rackNumber}-2",
            Kind = LocationKind.Rack,
            OperationalRole = LocationOperationalRole.Storage,
            RowCode = rowCode,
            RackNumber = rackNumber,
            PalletNumber = 2
        };
        var otherRackPosition = new Location
        {
            Code = $"{rowCode}-{rackNumber + 1}-1",
            Kind = LocationKind.Rack,
            OperationalRole = LocationOperationalRole.Storage,
            RowCode = rowCode,
            RackNumber = rackNumber + 1,
            PalletNumber = 1
        };
        db.AddRange(firstPosition, secondPosition, otherRackPosition);
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = product, Location = firstPosition, Quantity = 8m },
            new InventoryBalance { Product = product, Location = secondPosition, Quantity = 2m },
            new InventoryBalance { Product = product, Location = otherRackPosition, Quantity = 1m });

        var occurredAt = DateTimeOffset.UtcNow.AddHours(-1);
        var movement = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = InventoryMovementType.Transfer,
            ResponsibleUser = user,
            OccurredAt = occurredAt,
            RecordedAt = occurredAt
        };
        var line = new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            Quantity = 1m,
            LineNumber = 1,
            SourceLocation = firstPosition,
            DestinationLocation = secondPosition
        };
        line.BalanceChanges.Add(new InventoryBalanceChange
        {
            Location = firstPosition,
            DeltaQuantity = -1m,
            PreviousQuantity = 9m,
            ResultingQuantity = 8m
        });
        line.BalanceChanges.Add(new InventoryBalanceChange
        {
            Location = secondPosition,
            DeltaQuantity = 1m,
            PreviousQuantity = 1m,
            ResultingQuantity = 2m
        });
        movement.Lines.Add(line);
        var otherRackLine = new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            Quantity = 1m,
            LineNumber = 2,
            SourceLocation = secondPosition,
            DestinationLocation = otherRackPosition
        };
        otherRackLine.BalanceChanges.Add(new InventoryBalanceChange
        {
            Location = secondPosition,
            DeltaQuantity = -1m,
            PreviousQuantity = 3m,
            ResultingQuantity = 2m
        });
        otherRackLine.BalanceChanges.Add(new InventoryBalanceChange
        {
            Location = otherRackPosition,
            DeltaQuantity = 1m,
            PreviousQuantity = 0m,
            ResultingQuantity = 1m
        });
        movement.Lines.Add(otherRackLine);
        db.Add(movement);
        await db.SaveChangesAsync();

        var service = new HeatmapReportService(
            db,
            new WarehouseMapService(db),
            new WarehouseSettingsService(db));
        var report = await service.GetHeatmapPageAsync(new HeatmapReportFilter(
            Metric: HeatmapMetricType.AccessFrequency,
            FromUtc: occurredAt.AddMinutes(-1),
            ToUtc: occurredAt.AddMinutes(1),
            RowCode: rowCode));

        Assert.Equal(2, report.Racks.Count);
        var firstRack = report.Racks.Single(rack => rack.RackNumber == rackNumber);
        var otherRack = report.Racks.Single(rack => rack.RackNumber == rackNumber + 1);
        Assert.Equal(1, firstRack.AccessCount);
        Assert.Equal(2, firstRack.TotalPositions);
        Assert.Equal(2, firstRack.OccupiedPositions);
        Assert.Equal(1, otherRack.AccessCount);
    }

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
