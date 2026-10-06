using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionScheduleSkuTotalsTests
{
    [Fact]
    public async Task Open_total_preserves_processed_events_and_requires_pin_and_current_review()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, productId) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var monday = new DateOnly(2026, 10, 5);
        var id = (await service.CreateWeekAsync(new(Guid.NewGuid(), monday, actor))).Id!.Value;
        Assert.True((await service.SaveDraftChangesAsync(new(Guid.NewGuid(), id, 0,
            [new("add", null, null, new(monday, productId, 20, null, null, null, null)),
             new("add", null, null, new(monday, productId, 20, null, null, null, null))], actor))).Success);
        var week = (await service.GetWeekAsync(id))!;
        Assert.True((await service.PublishAsync(new(Guid.NewGuid(), id, week.Version, "4826", actor))).Success);
        week = (await service.GetWeekAsync(id))!;
        var lines = week.Lines.OrderBy(x => x.Sequence).ToArray();
        var order = await db.ProductionWorkOrders.Include(x => x.Stages).SingleAsync(x => x.Id == lines[1].WorkOrderId);
        var processed = new ProductionEvent
        {
            WorkOrderId = order.Id,
            WorkOrderStageId = order.Stages.First().Id,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "processed",
            ResponsibleUserId = actor,
            Type = ProductionEventType.Processed,
            Quantity = 8,
            GoodQuantity = 8
        };
        db.ProductionEvents.Add(processed); await db.SaveChangesAsync();
        var movements = await db.InventoryMovements.CountAsync();
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), id, week.Version, [], actor,
            SkuTotals: [new(monday, productId, 5, "PACK", null, null, null)]);
        var tooLow = await service.PreviewWorkspaceChangesAsync(command);
        Assert.False(tooLow.CanConfirm); Assert.Contains(tooLow.Errors, x => x.Contains("mínimo permitido"));
        command = command with { SkuTotals = [command.SkuTotals![0] with { Quantity = 30 }] };
        var review = await service.PreviewWorkspaceChangesAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        command = command with { ReviewedFingerprint = review.Fingerprint };
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await service.SaveWorkspaceChangesAsync(command, "0000")).Status);
        var result = await service.SaveWorkspaceChangesAsync(command, "4826");
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        Assert.Equal(8, (await db.ProductionEvents.SingleAsync(x => x.Id == processed.Id)).GoodQuantity);
        Assert.Equal(movements, await db.InventoryMovements.CountAsync());
        Assert.Equal(30, (await service.GetWeekAsync(id))!.Lines.Sum(x => x.Quantity));
        Assert.Equal(10, (await service.GetWeekAsync(id))!.Lines.Single(x => x.Id == lines[1].Id).Quantity);
    }

    [Fact]
    public async Task Daily_total_reduces_from_last_increases_first_and_retries_without_duplicates()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, productId) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var monday = new DateOnly(2026, 10, 5);
        var id = (await service.CreateWeekAsync(new(Guid.NewGuid(), monday, actor))).Id!.Value;
        var seed = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), id, 0,
            [new("add", null, null, new(monday, productId, 20, "OLD1", null, null, "first")),
             new("add", null, null, new(monday, productId, 20, "OLD2", null, null, "second"))], actor);
        Assert.True((await service.SaveDraftChangesAsync(seed)).Success);
        var before = (await service.GetWeekAsync(id))!;
        var ids = before.Lines.OrderBy(x => x.Sequence).Select(x => x.Id).ToArray();
        async Task Save(decimal quantity)
        {
            var week = (await service.GetWeekAsync(id))!;
            var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), id, week.Version, [], actor,
                SkuTotals: [new(monday, productId, quantity, "0001", "0002", "0003", "combined")]);
            var review = await service.PreviewWorkspaceChangesAsync(command);
            Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
            command = command with { ReviewedFingerprint = review.Fingerprint };
            var saved = await service.SaveWorkspaceChangesAsync(command);
            Assert.True(saved.Success, string.Join(" | ", saved.Errors ?? []));
            Assert.True((await service.SaveWorkspaceChangesAsync(command)).Success);
            Assert.Equal(ProductionDailyCommandStatus.IdempotencyConflict,
                (await service.SaveWorkspaceChangesAsync(command with { SkuTotals = [new(monday, productId, quantity + 1, null, null, null, null)] })).Status);
        }
        await Save(25);
        var reduced = (await service.GetWeekAsync(id))!.Lines;
        Assert.Equal(20, reduced.Single(x => x.Id == ids[0]).Quantity);
        Assert.Equal(5, reduced.Single(x => x.Id == ids[1]).Quantity);
        Assert.All(reduced, line => Assert.Equal("0003", line.OrderReference3));
        await Save(50);
        Assert.Equal(45, (await service.GetWeekAsync(id))!.Lines.Single(x => x.Id == ids[0]).Quantity);
        await Save(10);
        Assert.Single((await service.GetWeekAsync(id))!.Lines);
        Assert.True((await db.ProductionScheduleLines.SingleAsync(x => x.Id == ids[1])).IsCancelled);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Contains(await db.ProductionScheduleRevisions.ToListAsync(), x => x.BeforeJson.Contains("OLD1"));
    }

    [Fact]
    public async Task Total_validation_is_atomic_and_closed_or_stale_weeks_are_rejected()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, productId) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var monday = new DateOnly(2026, 10, 5);
        var id = (await service.CreateWeekAsync(new(Guid.NewGuid(), monday, actor))).Id!.Value;
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), id, 0, [], actor,
            SkuTotals: [new(monday, productId, 40, null, null, null, null), new(monday.AddDays(1), productId, -1, null, null, null, null)]);
        Assert.False((await service.PreviewWorkspaceChangesAsync(command)).CanConfirm);
        Assert.False((await service.SaveWorkspaceChangesAsync(command)).Success);
        Assert.Empty((await service.GetWeekAsync(id))!.Lines);
        command = command with { SkuTotals = [command.SkuTotals![0]] };
        var review = await service.PreviewWorkspaceChangesAsync(command);
        command = command with { ReviewedFingerprint = review.Fingerprint };
        var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == id);
        week.Version++; await db.SaveChangesAsync();
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict, (await service.SaveWorkspaceChangesAsync(command)).Status);
        week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == id);
        week.Status = ProductionScheduleWeekStatus.Closed; await db.SaveChangesAsync();
        Assert.False((await service.PreviewWorkspaceChangesAsync(command with { ExpectedWeekVersion = week.Version })).CanConfirm);
        Assert.False((await service.SaveWorkspaceChangesAsync(command with { ExpectedWeekVersion = week.Version })).Success);
    }

    [Fact]
    public async Task Existing_physical_links_retain_a_nonzero_minimum_without_deleting_history()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, productId) = await ProductionScheduleCarryoverCopyTests.SeedAsync(db);
        var monday = new DateOnly(2026, 10, 5);
        var id = (await service.CreateWeekAsync(new(Guid.NewGuid(), monday, actor))).Id!.Value;
        Assert.True((await service.SaveDraftChangesAsync(new(Guid.NewGuid(), id, 0,
            [new("add", null, null, new(monday, productId, 40, null, null, null, null))], actor))).Success);
        var week = (await service.GetWeekAsync(id))!;
        var line = week.Lines.Single();
        db.ProductionWeekOpenings.Add(new()
        {
            WeekId = Guid.NewGuid(),
            ProductId = productId,
            SourceWeekId = id,
            SourceLineId = line.Id,
            Area = ProductionDailyArea.Cutting,
            Quantity = 1
        });
        await db.SaveChangesAsync();
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), id, week.Version, [], actor,
            SkuTotals: [new(monday, productId, 0, null, null, null, null)]);
        var review = await service.PreviewWorkspaceChangesAsync(command);
        Assert.False(review.CanConfirm);
        Assert.Contains(review.Errors, error => error.Contains("mínimo permitido"));
        Assert.False((await service.SaveWorkspaceChangesAsync(command)).Success);
        Assert.Equal(40, (await service.GetWeekAsync(id))!.Lines.Single().Quantity);
    }
}
