using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionScheduleDraftBatchTests
{
    private const string PinKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Draft_group_adds_edits_and_removes_without_merging_skus_and_retry_is_idempotent()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var admin = new User { FullName = "Schedule admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "4826"); db.Users.Add(admin); await db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(db, pins,
            new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var monday = new DateOnly(2026, 9, 21);
        var weekId = (await service.CreateWeekAsync(new(Guid.NewGuid(), monday, admin.Id))).Id!.Value;
        var week = (await service.GetWeekAsync(weekId))!;
        Assert.True((await service.SaveBatchAsync(new(Guid.NewGuid(), weekId, week.Version,
            [new(monday, product.Id, 10, "ORDER-1", null, null, null),
             new(monday, product.Id, 20, "ORDER-2", null, null, null)], admin.Id))).Success);
        week = (await service.GetWeekAsync(weekId))!;
        var first = week.Lines.Single(x => x.OrderReference1 == "ORDER-1");
        var second = week.Lines.Single(x => x.OrderReference1 == "ORDER-2");
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), weekId, week.Version,
            [new("edit", first.Id, first.Version,
                new(monday, product.Id, 15, "ORDER-1", null, null, "changed")),
             new("remove", second.Id, second.Version, null),
             new("add", null, null,
                new(monday.AddDays(1), product.Id, 30, "ORDER-3", null, null, null))], admin.Id);
        Assert.True((await service.SaveDraftChangesAsync(command)).Success);
        Assert.True((await service.SaveDraftChangesAsync(command)).Success);
        Assert.Equal(ProductionDailyCommandStatus.IdempotencyConflict,
            (await service.SaveDraftChangesAsync(command with { ExpectedWeekVersion = week.Version + 1 })).Status);
        var updated = (await service.GetWeekAsync(weekId))!;
        Assert.Equal(2, updated.Lines.Count);
        Assert.Equal(15, updated.Lines.Single(x => x.Id == first.Id).Quantity);
        Assert.Equal("changed", updated.Lines.Single(x => x.Id == first.Id).Notes);
        Assert.Contains(updated.Lines, x => x.PlannedDate == monday.AddDays(1) && x.Quantity == 30);
        Assert.True((await db.ProductionScheduleLines.SingleAsync(x => x.Id == second.Id)).IsCancelled);
        Assert.All(updated.Lines, x => Assert.Null(x.WorkOrderId));
        Assert.Single(await db.ProductionScheduleRevisions.Where(x => x.OperationId == command.OperationId).ToListAsync());
    }

    [Fact]
    public async Task Draft_group_prevalidates_all_changes_and_rejects_101_and_open_week()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var admin = new User { FullName = "Schedule admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "4826"); db.Users.Add(admin); await db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(db, pins,
            new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var monday = new DateOnly(2026, 9, 21);
        var weekId = (await service.CreateWeekAsync(new(Guid.NewGuid(), monday, admin.Id))).Id!.Value;
        var week = (await service.GetWeekAsync(weekId))!;
        var valid = new ProductionScheduleDraftChange("add", null, null,
            new(monday, product.Id, 10, null, null, null, null));
        var invalid = new ProductionScheduleDraftChange("add", null, null,
            new(monday.AddDays(1), product.Id, 0, null, null, null, null));
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), weekId, week.Version,
            [valid, invalid], admin.Id);
        Assert.Equal(ProductionDailyCommandStatus.ValidationFailed,
            (await service.SaveDraftChangesAsync(command)).Status);
        Assert.Empty((await service.GetWeekAsync(weekId))!.Lines);
        Assert.Equal(ProductionDailyCommandStatus.ValidationFailed,
            (await service.SaveDraftChangesAsync(command with { Changes = Enumerable.Repeat(valid, 101).ToArray() })).Status);
        Assert.True((await service.SaveLineAsync(new(Guid.NewGuid(), weekId, null, week.Version,
            null, monday, product.Id, 10, null, null, null, null, admin.Id))).Success);
        week = (await service.GetWeekAsync(weekId))!;
        Assert.True((await service.PublishAsync(new(Guid.NewGuid(), weekId, week.Version, "4826", admin.Id))).Success);
        week = (await service.GetWeekAsync(weekId))!;
        Assert.Equal(ProductionDailyCommandStatus.ValidationFailed,
            (await service.SaveDraftChangesAsync(command with { ExpectedWeekVersion = week.Version, Changes = [valid] })).Status);
    }

    [Fact]
    public async Task Draft_group_accepts_exactly_100_daily_lines_without_collapsing_repetitions()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var admin = new User { FullName = "Schedule admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "4826"); db.Users.Add(admin); await db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(db, pins,
            new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var monday = new DateOnly(2026, 9, 21);
        var weekId = (await service.CreateWeekAsync(new(Guid.NewGuid(), monday, admin.Id))).Id!.Value;
        var changes = Enumerable.Range(1, 100).Select(index => new ProductionScheduleDraftChange("add", null, null,
            new(monday, product.Id, index, $"ORDER-{index}", null, null, null))).ToArray();
        var result = await service.SaveDraftChangesAsync(new(Guid.NewGuid(), weekId, 0, changes, admin.Id));
        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
        var lines = (await service.GetWeekAsync(weekId))!.Lines;
        Assert.Equal(100, lines.Count);
        Assert.Equal(100, lines.Select(x => x.Id).Distinct().Count());
        Assert.Equal(100, lines.Select(x => x.OrderReference1).Distinct().Count());
    }

    private static WarehouseDbContext Context() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase($"ProductionDraft-{Guid.NewGuid():N}").Options);
}
