using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class OperationalAlertServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Snapshot_separates_audiences_and_counts_conditions_across_lots()
    {
        await using var db = CreateDbContext();
        var user = User();
        var assigned = Product("ALERT-NEG", minimum: 1m);
        var restricted = Product("ALERT-REST");
        var inactiveRestricted = Product("ALERT-INACTIVE");
        var absent = Product("ALERT-ABSENT");
        var regular = new Location { Code = "A-01-01", Kind = LocationKind.Rack };
        var review = new Location { Code = "A-01-02", Kind = LocationKind.Rack };
        var blocked = new Location { Code = "B-01-01", Kind = LocationKind.Rack, IsBlocked = true };
        var inactive = new Location { Code = "B-01-02", Kind = LocationKind.Rack, IsActive = false };
        var retired = new Location { Code = "R-01-01", Kind = LocationKind.Rack, IsPhysicallyPresent = false };
        db.AddRange(user, assigned, restricted, inactiveRestricted, absent, regular, review, blocked, inactive, retired);
        db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = assigned, Location = regular });
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = assigned, Location = regular, LotId = Guid.NewGuid(), Quantity = 2m },
            new InventoryBalance { Product = assigned, Location = regular, LotId = Guid.NewGuid(), Quantity = -5m },
            new InventoryBalance { Product = restricted, Location = blocked, Quantity = 4m },
            new InventoryBalance { Product = inactiveRestricted, Location = inactive, Quantity = 2m },
            new InventoryBalance { Product = absent, Location = retired, Quantity = -10m });
        var campaign = new CycleCountCampaign { OperationId = Guid.NewGuid(), Number = 1, CreatedByUser = user };
        campaign.Locations.Add(new CycleCountLocation { Location = regular, Status = CycleCountLocationStatus.Stale });
        campaign.Locations.Add(new CycleCountLocation { Location = review, Status = CycleCountLocationStatus.UnderReview });
        db.CycleCountCampaigns.Add(campaign);
        await db.SaveChangesAsync();

        var service = Service(db);
        var publicSnapshot = await service.GetSnapshotAsync(OperationalAlertAudience.Public);
        var adminSnapshot = await service.GetSnapshotAsync(OperationalAlertAudience.Admin);
        var conditions = await service.GetActiveConditionsAsync();

        Assert.Equal(2, publicSnapshot.Items.Count);
        Assert.All(publicSnapshot.Items, item => Assert.Contains(item.Category,
            new[] { OperationalAlertCategory.NegativeInventory, OperationalAlertCategory.BelowMinimum }));
        Assert.Equal(1, publicSnapshot.Items.Single(x => x.Category == OperationalAlertCategory.NegativeInventory).Count);
        Assert.DoesNotContain(publicSnapshot.Items, x => x.Category == OperationalAlertCategory.RestrictedInventory);
        Assert.All(publicSnapshot.Items, item => Assert.StartsWith("/Reports/Inventory?view=exceptions&exception=", item.TargetUrl, StringComparison.Ordinal));
        Assert.All(adminSnapshot.Items, item => Assert.Equal($"/Admin/Inventory/Alerts?category={item.Category}", item.TargetUrl));
        Assert.Equal(2, adminSnapshot.Items.Single(x => x.Category == OperationalAlertCategory.UnassignedBalance).Count);
        Assert.Equal(2, adminSnapshot.Items.Single(x => x.Category == OperationalAlertCategory.RestrictedInventory).Count);
        Assert.Equal(1, adminSnapshot.Items.Single(x => x.Category == OperationalAlertCategory.CycleCountStale).Count);
        Assert.Equal(1, adminSnapshot.Items.Single(x => x.Category == OperationalAlertCategory.CycleCountPending).Count);
        Assert.Equal(adminSnapshot.Items.Sum(x => x.Count), adminSnapshot.TotalVisible);
        Assert.Equal(Now, adminSnapshot.GeneratedAtUtc);
        Assert.Contains(conditions, item => item.Category == OperationalExceptionCategory.NegativeInventory && item.ReasonText.Contains("-3 EA", StringComparison.Ordinal));
        Assert.Contains(conditions, item => item.Category == OperationalExceptionCategory.BelowMinimum && item.ReasonText.Contains("mínimo de 1 EA", StringComparison.Ordinal));
        Assert.Contains(conditions, item => item.Category == OperationalExceptionCategory.UnassignedBalance && item.ReasonText.Contains("sin una asignación activa", StringComparison.Ordinal));
        Assert.Contains(conditions, item => item.Category == OperationalExceptionCategory.RestrictedInventory && item.ReasonText.Contains("ubicación bloqueada", StringComparison.Ordinal));
        Assert.Contains(conditions, item => item.Category == OperationalExceptionCategory.RestrictedInventory && item.ReasonText.Contains("ubicación inactiva", StringComparison.Ordinal));
        Assert.Contains(conditions, item => item.Category == OperationalExceptionCategory.StagnantInventory && item.ReasonText.Contains("no existe una salida efectiva", StringComparison.Ordinal));
        Assert.Contains(conditions, item => item.Category == OperationalExceptionCategory.CycleCountStale && item.ReasonText.Contains("requiere reconteo", StringComparison.Ordinal));
        Assert.Contains(conditions, item => item.Category == OperationalExceptionCategory.CycleCountPending && item.ReasonText.Contains("pendiente de revisión", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wip_reminder_uses_positive_balance_and_oldest_positive_lot_not_legacy_dispositions()
    {
        await using var db = CreateDbContext();
        var user = User();
        var product = Product("ALERT-WIP");
        var recentProduct = Product("ALERT-WIP-RECENT");
        var wip = new Location { Code = "WIP-01", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var oldLot = new ProductLot { Product = product, Number = "OLD", NormalizedNumber = "OLD", LotDate = DateOnly.FromDateTime(Now.AddDays(-8).Date) };
        var newerLot = new ProductLot { Product = product, Number = "NEWER", NormalizedNumber = "NEWER", LotDate = DateOnly.FromDateTime(Now.AddDays(-1).Date) };
        var recentLot = new ProductLot { Product = recentProduct, Number = "RECENT", NormalizedNumber = "RECENT", LotDate = DateOnly.FromDateTime(Now.AddDays(-2).Date) };
        db.AddRange(user, product, recentProduct, wip, oldLot, newerLot, recentLot);
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = product, Location = wip, Lot = oldLot, Quantity = 6m },
            new InventoryBalance { Product = product, Location = wip, Lot = newerLot, Quantity = 2m },
            new InventoryBalance { Product = recentProduct, Location = wip, Lot = recentLot, Quantity = 4m });
        await db.SaveChangesAsync();

        var snapshot = await Service(db).GetSnapshotAsync(OperationalAlertAudience.Admin);
        var reminder = snapshot.Items.Single(x => x.Category == OperationalAlertCategory.AgedWip);
        var page = await Service(db).GetPageAsync(OperationalAlertCategory.AgedWip, null, 1, 25);
        var conditions = await Service(db).GetActiveConditionsAsync();

        Assert.Equal(1, reminder.Count);
        Assert.Equal("Saldo WIP estancado", reminder.Title);
        Assert.Equal(1, page.TotalCount);
        Assert.Contains(page.Items, x => x.PrimaryText == "ALERT-WIP" && x.ValueText!.Contains('8'));
        Assert.All(page.Items, x => Assert.Contains("/Reports/Wip?attention=aged", x.TargetUrl, StringComparison.Ordinal));
        var condition = Assert.Single(conditions, item => item.Category == OperationalExceptionCategory.AgedWip);
        Assert.Contains("17/08/2026", condition.ReasonText, StringComparison.Ordinal);
        Assert.Contains("límite configurado de 7 días", condition.ReasonText, StringComparison.Ordinal);
    }

    private static OperationalAlertService Service(WarehouseDbContext db) =>
        new(db, new WarehouseSettingsService(db), new WarehouseClock(new WarehouseSettingsService(db)), new FixedTimeProvider(Now));

    private static InventoryMovementLine Line(InventoryMovement movement, Product product, Location source, int number, decimal quantity)
    {
        var line = new InventoryMovementLine { Movement = movement, Product = product, SourceLocation = source, UnitId = 1, LineNumber = number, Quantity = quantity };
        movement.Lines.Add(line);
        return line;
    }

    private static WipDisposition Disposition(InventoryMovementLine line, User user, decimal quantity, string fingerprint) => new()
    {
        OperationId = Guid.NewGuid(),
        RequestFingerprint = fingerprint,
        OriginalMovementLine = line,
        Type = WipDispositionType.WarehouseReturn,
        Quantity = quantity,
        ResponsibleUser = user,
        OccurredAt = Now.AddDays(-1)
    };

    private static User User() => new()
    {
        FullName = $"Operador alertas {Guid.NewGuid():N}",
        PinLookup = Guid.NewGuid().ToString("N"),
        PinHash = "hash",
        RoleId = 2
    };

    private static Product Product(string sku, decimal minimum = 0m) => new()
    {
        Sku = sku,
        BaseUnitId = 1,
        MinimumStock = minimum,
        IsActive = true
    };

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"OperationalAlertTests-{Guid.NewGuid():N}").Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
