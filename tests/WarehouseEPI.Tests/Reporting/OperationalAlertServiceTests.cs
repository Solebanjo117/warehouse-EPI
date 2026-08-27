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
        var absent = Product("ALERT-ABSENT");
        var regular = new Location { Code = "A-01-01", Kind = LocationKind.Rack };
        var blocked = new Location { Code = "B-01-01", Kind = LocationKind.Rack, IsBlocked = true };
        var retired = new Location { Code = "R-01-01", Kind = LocationKind.Rack, IsPhysicallyPresent = false };
        db.AddRange(user, assigned, restricted, absent, regular, blocked, retired);
        db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = assigned, Location = regular });
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = assigned, Location = regular, LotId = Guid.NewGuid(), Quantity = 2m },
            new InventoryBalance { Product = assigned, Location = regular, LotId = Guid.NewGuid(), Quantity = -5m },
            new InventoryBalance { Product = restricted, Location = blocked, Quantity = 4m },
            new InventoryBalance { Product = absent, Location = retired, Quantity = -10m });
        var campaign = new CycleCountCampaign { OperationId = Guid.NewGuid(), Number = 1, CreatedByUser = user };
        campaign.Locations.Add(new CycleCountLocation { Location = regular, Status = CycleCountLocationStatus.Stale });
        db.CycleCountCampaigns.Add(campaign);
        await db.SaveChangesAsync();

        var service = Service(db);
        var publicSnapshot = await service.GetSnapshotAsync(OperationalAlertAudience.Public);
        var adminSnapshot = await service.GetSnapshotAsync(OperationalAlertAudience.Admin);

        Assert.Equal(2, publicSnapshot.Items.Count);
        Assert.All(publicSnapshot.Items, item => Assert.Contains(item.Category,
            new[] { OperationalAlertCategory.NegativeInventory, OperationalAlertCategory.BelowMinimum }));
        Assert.Equal(1, publicSnapshot.Items.Single(x => x.Category == OperationalAlertCategory.NegativeInventory).Count);
        Assert.DoesNotContain(publicSnapshot.Items, x => x.Category == OperationalAlertCategory.RestrictedInventory);
        Assert.Equal(1, adminSnapshot.Items.Single(x => x.Category == OperationalAlertCategory.UnassignedBalance).Count);
        Assert.Equal(1, adminSnapshot.Items.Single(x => x.Category == OperationalAlertCategory.RestrictedInventory).Count);
        Assert.Equal(1, adminSnapshot.Items.Single(x => x.Category == OperationalAlertCategory.CycleCountStale).Count);
        Assert.Equal(adminSnapshot.Items.Sum(x => x.Count), adminSnapshot.TotalVisible);
        Assert.Equal(Now, adminSnapshot.GeneratedAtUtc);
    }

    [Fact]
    public async Task Wip_reminder_ignores_effective_return_but_includes_reverted_return()
    {
        await using var db = CreateDbContext();
        var user = User();
        var product = Product("ALERT-WIP");
        var source = new Location { Code = "A-02-01", Kind = LocationKind.Rack };
        var wip = new Location { Code = "WIP-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Wip };
        db.AddRange(user, product, source, wip);
        var movement = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "aged-wip",
            Type = InventoryMovementType.Exit,
            Purpose = InventoryMovementPurpose.ProductionIssue,
            OperationalArea = wip,
            ResponsibleUser = user,
            OccurredAt = Now.AddDays(-8)
        };
        var pending = Line(movement, product, source, 1, 2m);
        var returned = Line(movement, product, source, 2, 3m);
        var reverted = Line(movement, product, source, 3, 4m);
        db.InventoryMovements.Add(movement);
        var effectiveReturn = Disposition(returned, user, 3m, "return-effective");
        var originalReturn = Disposition(reverted, user, 4m, "return-reverted");
        var reversal = Disposition(reverted, user, 4m, "return-reversal");
        reversal.ReversesDisposition = originalReturn;
        db.WipDispositions.AddRange(effectiveReturn, originalReturn, reversal);
        await db.SaveChangesAsync();

        var snapshot = await Service(db).GetSnapshotAsync(OperationalAlertAudience.Admin);
        var reminder = snapshot.Items.Single(x => x.Category == OperationalAlertCategory.AgedWip);
        var page = await Service(db).GetPageAsync(OperationalAlertCategory.AgedWip, null, 1, 25);

        Assert.Equal(2, reminder.Count);
        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Items, x => x.TargetUrl.EndsWith(pending.Id.ToString(), StringComparison.Ordinal));
        Assert.Contains(page.Items, x => x.TargetUrl.EndsWith(reverted.Id.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(page.Items, x => x.TargetUrl.EndsWith(returned.Id.ToString(), StringComparison.Ordinal));
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
