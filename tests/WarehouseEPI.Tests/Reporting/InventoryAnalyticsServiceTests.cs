using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class InventoryAnalyticsServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Occupancy_applies_precedence_groups_lots_and_excludes_non_storage_positions()
    {
        await using var db = CreateDbContext();
        var productA = Product("OCC-A");
        var productB = Product("OCC-B");
        var inactive = Location("A-01", "A", isActive: false);
        var blocked = Location("A-02", "A", isBlocked: true);
        var negative = Location("A-03", "A");
        var occupied = Location("B-01", "B");
        var empty = Location("B-02", "B");
        var area = new Location { Code = "AREA-01", Kind = LocationKind.Area, RowCode = "AREA" };
        var wip = new Location
        {
            Code = "WIP-01",
            Kind = LocationKind.Rack,
            OperationalRole = LocationOperationalRole.Wip,
            RowCode = "WIP"
        };
        db.AddRange(productA, productB, inactive, blocked, negative, occupied, empty, area, wip);
        db.InventoryBalances.AddRange(
            Balance(productA, inactive, -1m),
            Balance(productA, blocked, -1m),
            Balance(productA, negative, 5m),
            Balance(productA, negative, -7m),
            Balance(productA, occupied, 5m),
            Balance(productA, occupied, -5m),
            Balance(productB, occupied, 2m),
            Balance(productA, area, 3m),
            Balance(productA, wip, 4m));
        await db.SaveChangesAsync();

        var report = await Service(db).GetOccupancyAsync();

        Assert.Equal(5, report.Summary.TotalStoragePositions);
        Assert.Equal(1, report.Summary.InactiveCount);
        Assert.Equal(1, report.Summary.BlockedCount);
        Assert.Equal(1, report.Summary.NegativeCount);
        Assert.Equal(1, report.Summary.OccupiedCount);
        Assert.Equal(1, report.Summary.EmptyCount);
        Assert.Equal(100m / 3m, report.Summary.UtilizationPercentage, 2);
        Assert.Equal(["A", "B"], report.Rows.Select(row => row.RowCode));
        Assert.Equal(3, report.Rows[0].Summary.TotalStoragePositions);
        Assert.Equal(2, report.Rows[1].Summary.TotalStoragePositions);
    }

    [Fact]
    public async Task Exit_activity_includes_zero_exit_products_filters_and_excludes_correction_chain()
    {
        await using var db = CreateDbContext();
        var user = User();
        var active = Product("ROT-A", description: "Guante azul", reference: "REF-A");
        var zero = Product("ROT-ZERO");
        var inactive = Product("ROT-INACTIVE", isActive: false, unitId: 2);
        var location = Location("ROT-01", "R");
        db.AddRange(user, active, zero, inactive, location);
        db.ProductBarcodes.Add(new ProductBarcode { Product = active, Barcode = "BAR-ACTIVITY-4287" });
        db.InventoryBalances.AddRange(Balance(active, location, 12m), Balance(zero, location, 3m));

        AddExit(db, user, active, new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero), 100m);
        var original = AddExit(db, user, active, NowUtc.AddDays(-10), 5m, 7m);
        var reversal = AddExit(db, user, active, NowUtc.AddDays(-9), 12m);
        var replacement = AddExit(db, user, active, NowUtc.AddDays(-8), 4m);
        var secondReversal = AddExit(db, user, active, NowUtc.AddDays(-7), 4m);
        var finalReplacement = AddExit(db, user, active, NowUtc.AddDays(-6), 3m);
        db.InventoryMovementCorrections.AddRange(
            Correction(original, reversal, replacement, user),
            Correction(replacement, secondReversal, finalReplacement, user));
        await db.SaveChangesAsync();

        var filter = new InventoryAnalyticsFilter(
            NowUtc.AddDays(-29),
            NowUtc.AddDays(1),
            ProductStatus: "active",
            PageSize: 25);
        var page = await Service(db).GetExitActivityPageAsync(filter);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal("ROT-A", page.Items[0].Sku);
        Assert.Equal(1, page.Items[0].EffectiveExitMovementCount);
        Assert.Equal(3m, page.Items[0].QuantityInBaseUnit);
        Assert.Equal(12m, page.Items[0].CurrentStock);
        Assert.Equal(finalReplacement.OccurredAt, page.Items[0].LastExitDateUtc);
        Assert.Equal("ROT-ZERO", page.Items[1].Sku);
        Assert.Equal(0, page.Items[1].EffectiveExitMovementCount);

        var search = await Service(db).GetExitActivityPageAsync(filter with { Search = "ref-a" });
        Assert.Single(search.Items);
        Assert.Equal("ROT-A", search.Items[0].Sku);
        var barcodeSearch = await Service(db).GetExitActivityPageAsync(filter with { Search = "activity-42" });
        Assert.Single(barcodeSearch.Items);
        Assert.Equal("ROT-A", barcodeSearch.Items[0].Sku);

        var inactiveOnly = await Service(db).GetExitActivityPageAsync(filter with { ProductStatus = "inactive" });
        Assert.Single(inactiveOnly.Items);
        Assert.False(inactiveOnly.Items[0].IsActive);

        var boxes = await Service(db).GetExitActivityPageAsync(filter with { ProductStatus = "all", UnitId = 2 });
        Assert.Single(boxes.Items);
        Assert.Equal("BX", boxes.Items[0].UnitCode);

        var allHistory = await Service(db).GetExitActivityPageAsync(filter with { FromUtc = null, ToUtc = null });
        Assert.Equal(2, allHistory.Items[0].EffectiveExitMovementCount);
        Assert.Equal(103m, allHistory.Items[0].QuantityInBaseUnit);
    }

    [Fact]
    public async Task Stagnant_uses_exact_local_boundaries_positive_stock_priority_and_paging()
    {
        await using var db = CreateDbContext();
        var user = User();
        var location = Location("STG-01", "S");
        var never = Product("STG-NEVER");
        var days90 = Product("STG-090");
        var days60 = Product("STG-060");
        var days30 = Product("STG-030");
        var recent = Product("STG-029");
        var noStock = Product("STG-NOSTOCK");
        var inactiveNever = Product("STG-INACTIVE", isActive: false);
        db.AddRange(user, location, never, days90, days60, days30, recent, noStock, inactiveNever);
        foreach (var product in new[] { never, days90, days60, days30, recent })
            db.InventoryBalances.Add(Balance(product, location, 1m));
        db.InventoryBalances.Add(Balance(noStock, location, 0m));
        db.InventoryBalances.Add(Balance(inactiveNever, location, 2m));
        AddExit(db, user, days90, LocalDayUtc(90), 1m);
        AddExit(db, user, days60, LocalDayUtc(60), 1m);
        AddExit(db, user, days30, LocalDayUtc(30), 1m);
        AddExit(db, user, recent, LocalDayUtc(29), 1m);
        AddExit(db, user, noStock, LocalDayUtc(100), 1m);
        await db.SaveChangesAsync();

        var page = await Service(db).GetStagnantPageAsync(
            new InventoryAnalyticsFilter(ProductStatus: "active", PageSize: 2),
            NowUtc);

        Assert.Equal(4, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(StagnantCategory.NeverExited, page.Items[0].Category);
        Assert.Equal(StagnantCategory.Days90Plus, page.Items[1].Category);

        var second = await Service(db).GetStagnantPageAsync(
            new InventoryAnalyticsFilter(ProductStatus: "active", PageNumber: 2, PageSize: 2),
            NowUtc);
        Assert.Equal([StagnantCategory.Days60To89, StagnantCategory.Days30To59],
            second.Items.Select(item => item.Category));
        Assert.DoesNotContain(second.Items, item => item.Sku is "STG-029" or "STG-NOSTOCK");

        var inactive = await Service(db).GetStagnantPageAsync(
            new InventoryAnalyticsFilter(ProductStatus: "inactive"),
            NowUtc);
        Assert.Single(inactive.Items);
        Assert.Equal("STG-INACTIVE", inactive.Items[0].Sku);
        Assert.False(inactive.Items[0].IsActive);
    }

    [Fact]
    public async Task Export_rejects_complete_result_when_limit_is_exceeded()
    {
        await using var db = CreateDbContext();
        db.Products.AddRange(Product("LIMIT-A"), Product("LIMIT-B"));
        await db.SaveChangesAsync();

        var batch = await Service(db).GetExitActivityExportAsync(new InventoryAnalyticsFilter(), maximumRows: 1);

        Assert.True(batch.ExceedsLimit);
        Assert.Equal(2, batch.TotalRows);
        Assert.Empty(batch.Items);
    }

    private static InventoryAnalyticsService Service(WarehouseDbContext db) =>
        new(db, new WarehouseSettingsService(db));

    private static DateTimeOffset LocalDayUtc(int daysAgo) =>
        new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.FromHours(-5)).AddDays(-daysAgo).ToUniversalTime();

    private static InventoryMovement AddExit(
        WarehouseDbContext db,
        User user,
        Product product,
        DateTimeOffset occurredAt,
        params decimal[] quantities)
    {
        var movement = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = InventoryMovementType.Exit,
            Purpose = InventoryMovementPurpose.ProductionIssue,
            ResponsibleUser = user,
            OccurredAt = occurredAt
        };
        var values = quantities.Length == 0 ? [1m] : quantities;
        for (var index = 0; index < values.Length; index++)
        {
            movement.Lines.Add(new InventoryMovementLine
            {
                Product = product,
                UnitId = 1,
                Quantity = values[index],
                LineNumber = index + 1
            });
        }
        db.InventoryMovements.Add(movement);
        return movement;
    }

    private static InventoryMovementCorrection Correction(
        InventoryMovement original,
        InventoryMovement reversal,
        InventoryMovement replacement,
        User user) => new()
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovement = original,
            ReversalMovement = reversal,
            ReplacementMovement = replacement,
            Reason = "Corrección analítica",
            RequestedByUser = user,
            AuthorizedByUser = user
        };

    private static InventoryBalance Balance(Product product, Location location, decimal quantity) => new()
    {
        Product = product,
        Location = location,
        LotId = Guid.NewGuid(),
        Quantity = quantity
    };

    private static Location Location(string code, string row, bool isActive = true, bool isBlocked = false) => new()
    {
        Code = code,
        Kind = LocationKind.Rack,
        OperationalRole = LocationOperationalRole.Storage,
        RowCode = row,
        IsActive = isActive,
        IsBlocked = isBlocked
    };

    private static Product Product(
        string sku,
        bool isActive = true,
        string? description = null,
        string? reference = null,
        short unitId = 1) => new()
        {
            Sku = sku,
            Description = description,
            ExternalReference = reference,
            BaseUnitId = unitId,
            IsActive = isActive
        };

    private static User User() => new()
    {
        FullName = $"Analista {Guid.NewGuid():N}",
        PinLookup = Guid.NewGuid().ToString("N"),
        PinHash = "hash",
        RoleId = 2
    };

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"InventoryAnalyticsTests-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
