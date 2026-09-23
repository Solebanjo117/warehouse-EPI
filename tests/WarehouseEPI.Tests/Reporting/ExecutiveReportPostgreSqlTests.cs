using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Reporting;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class ExecutiveReportPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task PostgreSql_aggregates_effective_movements_and_exclusive_rack_states()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"EXEC-{suffix}", $"EXEC-AREA-{suffix}", "7319");
        await using var db = fixture.CreateDbContext();
        var settings = new WarehouseSettingsService(db);
        var fixedTime = new FixedTimeProvider(new DateTimeOffset(2035, 6, 15, 18, 0, 0, TimeSpan.Zero));
        var service = new ExecutiveReportService(
            db, settings, new InventoryAnalyticsService(db, settings), fixedTime);
        var interval = new ExecutiveReportFilter(
            new DateTimeOffset(2035, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2035, 7, 1, 0, 0, 0, TimeSpan.Zero),
            "Junio 2035",
            new DateTimeOffset(2035, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2035, 6, 1, 0, 0, 0, TimeSpan.Zero),
            "Mayo 2035");
        var before = await service.GetExecutiveReportAsync(interval);

        var product = await db.Products.SingleAsync(item => item.Id == seed.ProductId);
        var user = await db.Users.SingleAsync(item => item.FullName == $"Operador EXEC-{suffix}");
        var occupied = await db.Locations.SingleAsync(item => item.Id == seed.LocationId);
        occupied.Kind = LocationKind.Rack;
        occupied.OperationalRole = LocationOperationalRole.Storage;
        occupied.RowCode = "E";
        occupied.RackNumber = 1;

        var secondProduct = new Product { Sku = $"EXEC2-{suffix}", BaseUnitId = 1 };
        var negative = Rack($"EXEC-NEG-{suffix}", 2);
        var blocked = Rack($"EXEC-BLK-{suffix}", 3, isBlocked: true);
        var empty = Rack($"EXEC-EMP-{suffix}", 4);
        db.AddRange(secondProduct, negative, blocked, empty);
        db.InventoryBalances.AddRange(
            new InventoryBalance { ProductId = product.Id, LocationId = occupied.Id, Quantity = 10m },
            new InventoryBalance { ProductId = product.Id, LocationId = negative.Id, Quantity = 10m },
            new InventoryBalance { ProductId = secondProduct.Id, LocationId = negative.Id, Quantity = -1m },
            new InventoryBalance { ProductId = product.Id, LocationId = blocked.Id, Quantity = 5m });

        AddMovement(db, user, InventoryMovementType.Transfer, new DateTimeOffset(2035, 6, 10, 10, 0, 0, TimeSpan.Zero),
            (product, occupied, negative, 1m), (secondProduct, negative, occupied, 2m), (product, occupied, negative, 3m));
        var original = AddMovement(db, user, InventoryMovementType.Exit, new DateTimeOffset(2035, 6, 11, 10, 0, 0, TimeSpan.Zero),
            (product, null, occupied, 7m));
        var reversal = AddMovement(db, user, InventoryMovementType.Entry, new DateTimeOffset(2035, 6, 11, 10, 1, 0, TimeSpan.Zero),
            (product, occupied, null, 7m));
        var replacement = AddMovement(db, user, InventoryMovementType.Exit, new DateTimeOffset(2035, 6, 11, 10, 2, 0, TimeSpan.Zero),
            (product, null, occupied, 4m));
        db.InventoryMovementCorrections.Add(new InventoryMovementCorrection
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString(),
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovementId = original.Id,
            ReversalMovementId = reversal.Id,
            ReplacementMovementId = replacement.Id,
            Reason = "Corrección PostgreSQL",
            RequestedByUserId = user.Id,
            AuthorizedByUserId = user.Id,
            RecordedAt = new DateTimeOffset(2035, 6, 11, 10, 3, 0, TimeSpan.Zero)
        });
        await db.SaveChangesAsync();

        var report = await service.GetExecutiveReportAsync(interval);

        Assert.Equal(before.Capacity.TotalRackPositions + 4, report.Capacity.TotalRackPositions);
        Assert.Equal(before.Capacity.OccupiedPositions + 1, report.Capacity.OccupiedPositions);
        Assert.Equal(before.Capacity.NegativePositions + 1, report.Capacity.NegativePositions);
        Assert.Equal(before.Capacity.BlockedPositions + 1, report.Capacity.BlockedPositions);
        Assert.Equal(before.Capacity.EmptyPositions + 1, report.Capacity.EmptyPositions);
        Assert.Equal(report.Capacity.TotalRackPositions,
            report.Capacity.BlockedPositions + report.Capacity.NegativePositions +
            report.Capacity.OccupiedPositions + report.Capacity.EmptyPositions);
        Assert.Equal(2, report.OperationalFlow.TotalMovements);
        Assert.Equal(4, report.OperationalFlow.TotalDetails);
        Assert.Equal(1, report.OperationalFlow.TransferMovements);
        Assert.Equal(1, report.OperationalFlow.ExitMovements);
        Assert.Null(report.Comparison.TotalMovements.PercentChange);
        Assert.Contains(report.TopDemandedSkus,
            item => item.Sku == product.Sku && item.TotalQuantity == 4m && item.MovementCount == 1);
    }

    private static Location Rack(string code, short rack, bool isBlocked = false) => new()
    {
        Code = code,
        Kind = LocationKind.Rack,
        OperationalRole = LocationOperationalRole.Storage,
        RowCode = "E",
        RackNumber = rack,
        IsBlocked = isBlocked,
        BlockReason = isBlocked ? "Prueba de clasificación" : null
    };

    private static InventoryMovement AddMovement(
        WarehouseDbContext db,
        User user,
        InventoryMovementType type,
        DateTimeOffset occurredAt,
        params (Product Product, Location? Destination, Location? Source, decimal Quantity)[] lines)
    {
        var movement = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString(),
            Type = type,
            Purpose = type == InventoryMovementType.Exit
                ? InventoryMovementPurpose.GeneralExit : InventoryMovementPurpose.Standard,
            ResponsibleUserId = user.Id,
            OccurredAt = occurredAt,
            RecordedAt = occurredAt
        };
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            movement.Lines.Add(new InventoryMovementLine
            {
                LineNumber = index + 1,
                ProductId = line.Product.Id,
                UnitId = 1,
                Quantity = line.Quantity,
                SourceLocationId = line.Source?.Id,
                DestinationLocationId = line.Destination?.Id
            });
        }
        db.InventoryMovements.Add(movement);
        return movement;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
