using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class DailyDashboardServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Snapshot_uses_local_calendar_days_and_returns_all_fourteen_points()
    {
        await using var db = CreateDbContext();
        var user = User();
        var productA = Product("DASH-A");
        var productB = Product("DASH-B");
        db.AddRange(user, productA, productB);

        AddMovement(db, user, productA, InventoryMovementType.Entry, new(2026, 8, 20, 4, 59, 0, TimeSpan.Zero), 999m);
        AddMovement(db, user, productA, InventoryMovementType.Entry, new(2026, 8, 20, 5, 0, 0, TimeSpan.Zero), 1m);
        AddMovement(db, user, productB, InventoryMovementType.Transfer, new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero), 500m, productA);
        AddMovement(db, user, productA, InventoryMovementType.Adjustment, new(2026, 8, 20, 13, 0, 0, TimeSpan.Zero), 25m);
        await db.SaveChangesAsync();

        var snapshot = await Service(db).GetSnapshotAsync(NowUtc);

        Assert.Equal(new DateOnly(2026, 8, 20), snapshot.WarehouseDate);
        Assert.Equal(TimeSpan.FromHours(-5), snapshot.GeneratedAtLocal.Offset);
        Assert.Equal(14, snapshot.Metrics.RecentActivityTrend.Count);
        Assert.Equal(new DateOnly(2026, 8, 7), snapshot.Metrics.RecentActivityTrend[0].Date);
        Assert.Equal(new DateOnly(2026, 8, 20), snapshot.Metrics.RecentActivityTrend[^1].Date);

        var previousDay = snapshot.Metrics.RecentActivityTrend[^2];
        Assert.Equal(1, previousDay.EntryCount);
        Assert.Equal(1, previousDay.TotalEffectiveOperations);

        var today = snapshot.Metrics.RecentActivityTrend[^1];
        Assert.Equal(1, today.EntryCount);
        Assert.Equal(1, today.TransferCount);
        Assert.Equal(1, today.AdjustmentCount);
        Assert.Equal(3, today.TotalEffectiveOperations);
        Assert.Equal(2, today.DistinctSkusCount);
        Assert.Equal(3, snapshot.Metrics.EffectiveMovementsToday);
        Assert.Equal(1, snapshot.Metrics.EffectiveAdjustmentsToday);
        Assert.Contains(snapshot.Metrics.RecentActivityTrend, point => point.TotalEffectiveOperations == 0);
        var activity = await Service(db).GetActivityAsync(NowUtc, 90);
        Assert.Equal(90, activity.Points.Count);
        Assert.Equal(previousDay, activity.Points[^2]);
        Assert.Equal(today, activity.Points[^1]);
        var products = await Service(db).GetActivityProductsAsync(NowUtc, 14);
        Assert.Equal(4, products.TotalOperations);
        Assert.Equal(4, products.Items[0].Operations);
        Assert.Equal(100m, products.Items[0].Percent);
        Assert.Equal(25m, products.Items[1].Percent);
        Assert.NotNull(snapshot.Comparison);
        Assert.Equal(new MetricComparisonDto(3, 1, 2, 200m, MetricComparisonState.Increased), snapshot.Comparison.TodayOperations);
        Assert.Equal(MetricComparisonState.New, snapshot.Comparison.SevenDayOperations.State);
    }

    [Fact]
    public async Task Comparison_includes_previous_only_drivers_and_orders_their_negative_delta()
    {
        await using var db = CreateDbContext();
        var user = User();
        var oldProduct = Product("DASH-OLD");
        var currentProduct = Product("DASH-CURRENT");
        db.AddRange(user, oldProduct, currentProduct);
        AddMovement(db, user, oldProduct, InventoryMovementType.Exit, NowUtc.AddDays(-8), 1m);
        AddMovement(db, user, currentProduct, InventoryMovementType.Entry, NowUtc.AddDays(-1), 1m);
        await db.SaveChangesAsync();

        var comparison = Assert.IsType<OperationalComparisonDto>((await Service(db).GetSnapshotAsync(NowUtc)).Comparison);

        Assert.Contains(comparison.Products, item => item.Code == "DASH-OLD" && item.Current == 0 && item.Previous == 1 && item.Delta == -1);
        Assert.Contains(comparison.Products, item => item.Code == "DASH-CURRENT" && item.Current == 1 && item.Previous == 0 && item.Delta == 1);
        Assert.Equal("DASH-CURRENT", comparison.Products[0].Code);
    }

    [Fact]
    public async Task Snapshot_excludes_originals_and_reversals_across_correction_chains()
    {
        await using var db = CreateDbContext();
        var user = User();
        var product = Product("DASH-CORRECTION");
        db.AddRange(user, product);

        var original = AddMovement(db, user, product, InventoryMovementType.Entry, NowUtc.AddHours(-4), 1m);
        var reversal1 = AddMovement(db, user, product, InventoryMovementType.Exit, NowUtc.AddHours(-3), 1m);
        var replacement1 = AddMovement(db, user, product, InventoryMovementType.Transfer, NowUtc.AddHours(-2), 1m);
        var reversal2 = AddMovement(db, user, product, InventoryMovementType.Transfer, NowUtc.AddHours(-1), 1m);
        var replacement2 = AddMovement(db, user, product, InventoryMovementType.Adjustment, NowUtc, 1m);
        db.InventoryMovementCorrections.AddRange(
            Correction(original, reversal1, replacement1, user, "first"),
            Correction(replacement1, reversal2, replacement2, user, "second"));
        await db.SaveChangesAsync();

        var snapshot = await Service(db).GetSnapshotAsync(NowUtc);
        var today = snapshot.Metrics.RecentActivityTrend[^1];

        Assert.Equal(1, today.TotalEffectiveOperations);
        Assert.Equal(0, today.EntryCount);
        Assert.Equal(0, today.ExitCount);
        Assert.Equal(0, today.TransferCount);
        Assert.Equal(1, today.AdjustmentCount);
        Assert.Equal(1, snapshot.Metrics.EffectiveAdjustmentsToday);
        var activity = await Service(db).GetActivityAsync(NowUtc, 90);
        Assert.Equal(today, activity.Points[^1]);
        var products = await Service(db).GetActivityProductsAsync(NowUtc, 90);
        Assert.Equal(1, products.TotalOperations);
        var only = Assert.Single(products.Items);
        Assert.Equal(1, only.AdjustmentCount);
        Assert.Equal(1, only.Operations);
    }

    [Fact]
    public async Task Snapshot_groups_lots_for_negative_positions_and_counts_only_active_low_stock_products()
    {
        await using var db = CreateDbContext();
        var low = Product("DASH-LOW", minimum: 5m);
        var zeroBelowMinimum = Product("DASH-ZERO", minimum: 1m);
        var zeroAtMinimum = Product("DASH-NO-MIN", minimum: 0m);
        var inactive = Product("DASH-INACTIVE", minimum: 100m, isActive: false);
        var location = new Location { Code = "DASH-RACK", Kind = LocationKind.Rack };
        db.AddRange(low, zeroBelowMinimum, zeroAtMinimum, inactive, location);
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = low, Location = location, LotId = Guid.NewGuid(), Quantity = 3m },
            new InventoryBalance { Product = low, Location = location, LotId = Guid.NewGuid(), Quantity = -5m },
            new InventoryBalance { Product = zeroAtMinimum, Location = location, Quantity = 0m });
        await db.SaveChangesAsync();

        var snapshot = await Service(db).GetSnapshotAsync(NowUtc);

        Assert.Equal(1, snapshot.Metrics.NegativePositionsCount);
        Assert.Equal(2, snapshot.Metrics.LowStockProductsCount);
    }

    [Fact]
    public async Task Activity_paginates_products_deduplicates_lines_and_limits_locations()
    {
        await using var db = CreateDbContext();
        var user = User();
        var products = Enumerable.Range(0, 12).Select(i => Product($"RANK-{i:00}")).ToArray();
        db.Add(user); db.AddRange(products);
        for (var i = 0; i < products.Length; i++)
            AddMovement(db, user, products[i], InventoryMovementType.Entry, NowUtc, 1, products[i]);
        var locations = Enumerable.Range(0, 7).Select(i => new Location { Code = $"RANK-L{i}", Kind = LocationKind.Area }).ToArray();
        db.AddRange(locations);
        foreach (var location in locations)
        {
            var movement = AddMovement(db, user, products[0], InventoryMovementType.Transfer, NowUtc, 1, products[0]);
            foreach (var line in movement.Lines) { line.SourceLocation = location; line.DestinationLocation = location; }
        }
        // Exclude both sides of the complete local-day interval, rather than using UTC dates.
        AddMovement(db, user, products[0], InventoryMovementType.Exit, new(2026, 8, 14, 4, 59, 59, TimeSpan.Zero), 1);
        AddMovement(db, user, products[0], InventoryMovementType.Exit, new(2026, 8, 21, 5, 0, 0, TimeSpan.Zero), 1);
        await db.SaveChangesAsync();
        var service = Service(db);
        var first = await service.GetActivityProductsAsync(NowUtc, 7);
        Assert.Equal(19, first.TotalOperations);
        Assert.Equal(12, first.TotalProducts);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(8, first.Items[0].Operations);
        Assert.Equal(7, first.Items[0].TransferCount);
        Assert.Equal(5, first.Items[0].Locations.Count);
        Assert.All(first.Items[0].Locations, item => Assert.Equal(1, item.Operations));
        var last = await service.GetActivityProductsAsync(NowUtc, 7, int.MaxValue);
        Assert.Equal(2, last.PageNumber);
        Assert.Equal(new[] { "RANK-10", "RANK-11" }, last.Items.Select(item => item.Sku));
        var activity = await service.GetActivityAsync(NowUtc, 7);
        Assert.Equal(19, activity.Points.Sum(day => day.TotalEffectiveOperations));
        Assert.Equal(12, activity.Points[^1].DistinctSkusCount);
        Assert.All(activity.Points.Take(6), point => Assert.Equal(0, point.TotalEffectiveOperations));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetActivityAsync(NowUtc, 91));
    }

    private static DailyDashboardService Service(WarehouseDbContext db) =>
        new(db, new WarehouseSettingsService(db));

    private static InventoryMovement AddMovement(
        WarehouseDbContext db,
        User user,
        Product product,
        InventoryMovementType type,
        DateTimeOffset occurredAt,
        decimal quantity,
        Product? secondProduct = null)
    {
        var movement = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Guid.NewGuid().ToString("N"),
            Type = type,
            ResponsibleUser = user,
            OccurredAt = occurredAt
        };
        movement.Lines.Add(new InventoryMovementLine
        {
            Product = product,
            UnitId = 1,
            Quantity = quantity,
            LineNumber = 1
        });
        if (secondProduct is not null)
        {
            movement.Lines.Add(new InventoryMovementLine
            {
                Product = secondProduct,
                UnitId = 1,
                Quantity = quantity * 10m,
                LineNumber = 2
            });
        }
        db.InventoryMovements.Add(movement);
        return movement;
    }

    private static InventoryMovementCorrection Correction(
        InventoryMovement original,
        InventoryMovement reversal,
        InventoryMovement replacement,
        User user,
        string fingerprint) => new()
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = fingerprint,
            Type = InventoryMovementCorrectionType.Replacement,
            OriginalMovement = original,
            ReversalMovement = reversal,
            ReplacementMovement = replacement,
            Reason = "Corrección de prueba",
            RequestedByUser = user,
            AuthorizedByUser = user
        };

    private static User User() => new()
    {
        FullName = $"Operador tablero {Guid.NewGuid():N}",
        PinLookup = Guid.NewGuid().ToString("N"),
        PinHash = "hash",
        RoleId = 2
    };

    private static Product Product(string sku, decimal minimum = 0m, bool isActive = true) => new()
    {
        Sku = sku,
        BaseUnitId = 1,
        MinimumStock = minimum,
        IsActive = isActive
    };

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"DailyDashboardTests-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
