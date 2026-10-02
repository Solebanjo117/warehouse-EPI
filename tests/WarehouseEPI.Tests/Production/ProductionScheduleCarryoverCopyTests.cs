using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionScheduleCarryoverCopyTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Theory]
    [InlineData(true, 0, 100)]
    [InlineData(true, 40, 60)]
    [InlineData(false, 0, 100)]
    [InlineData(false, 40, 60)]
    public async Task Copy_owes_work_in_each_area_even_without_upstream_receipts(bool explicitSource, int sewn, int pending)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await SeedAsync(db);
        var source = await WeekAsync(service, actor, product, new(2026, 9, 14), 100);
        (await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == source.Id)).ExplicitCarryover = explicitSource;
        await AddCaptureAsync(db, source.Id, product, actor, ProductionDailyArea.Cutting, 100);
        if (sewn > 0) await AddCaptureAsync(db, source.Id, product, actor, ProductionDailyArea.Sewing, sewn);
        await AddCaptureAsync(db, source.Id, product, actor, ProductionDailyArea.Sewing, 9, true);
        var target = await WeekAsync(service, actor, product, source.WeekStart.AddDays(7), 0);
        var options = (await new ProductionWeekOpeningService(db).OptionsAsync(target.Id)).Where(x => x.SourceWeekId == source.Id).ToArray();
        Assert.DoesNotContain(options, x => x.Area == ProductionDailyArea.Cutting);
        Assert.Equal(pending, Assert.Single(options, x => x.Area == ProductionDailyArea.Sewing).Available);
        Assert.Equal(100, Assert.Single(options, x => x.Area == ProductionDailyArea.ReadyToPack).Available);
        Assert.Empty(await db.ProductionWeekOpenings.ToListAsync());
        Assert.Equal(2, await db.ProductionScheduleWeeks.CountAsync());
    }

    [Fact]
    public async Task Selected_week_inherits_its_roots_and_subtracts_work_and_other_destinations()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await SeedAsync(db);
        var first = await WeekAsync(service, actor, product, new(2026, 9, 7), 100);
        var middle = await WeekAsync(service, actor, product, first.WeekStart.AddDays(7), 50, false);
        var options = (await new ProductionWeekOpeningService(db).OptionsAsync(middle.Id)).Where(x => x.Area == ProductionDailyArea.Sewing).ToArray();
        var changes = options.Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Available, x.Fingerprint)).ToArray();
        Assert.True((await service.SaveDraftChangesAsync(new(Guid.NewGuid(), middle.Id, middle.Version, [], actor, changes))).Success);
        middle = (await service.GetWeekAsync(middle.Id))!;
        Assert.True((await service.PublishAsync(new(Guid.NewGuid(), middle.Id, middle.Version, "4826", actor))).Success);
        await AddCaptureAsync(db, middle.Id, product, actor, ProductionDailyArea.Sewing, 40);
        var target = await WeekAsync(service, actor, product, middle.WeekStart.AddDays(7), 0, false);
        var carried = (await new ProductionWeekOpeningService(db).OptionsAsync(target.Id))
            .Where(x => x.SourceWeekId == middle.Id && x.Area == ProductionDailyArea.Sewing).ToArray();
        Assert.Equal(110, carried.Sum(x => x.Available));
        Assert.Contains(carried, x => x.OriginalStart == first.WeekStart && x.SourceStart == middle.WeekStart && x.Available == 60);
        Assert.True((await service.SaveDraftChangesAsync(new(Guid.NewGuid(), target.Id, target.Version, [], actor,
            carried.Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Available, x.Fingerprint)).ToArray()))).Success);
        var balance = await new ProductionDailyBalanceService(db).GetDailySummaryAsync(target.Id, new(target.WeekStart));
        Assert.Equal(110, Assert.Single(balance!.Products).Sewing.Opening);
        Assert.Equal(2, await db.ProductionWorkOrders.CountAsync());
        var another = await WeekAsync(service, actor, product, target.WeekStart.AddDays(7), 0, false);
        Assert.DoesNotContain(await new ProductionWeekOpeningService(db).OptionsAsync(another.Id),
            x => x.SourceWeekId == middle.Id && x.Area == ProductionDailyArea.Sewing);
        var progress = Assert.Single(await new ProductionDailyBalanceService(db).GetScheduleProgressAsync(target.Id));
        Assert.Equal(110, Assert.Single(progress.Openings).Quantity);
        Assert.All(progress.Days, day => Assert.Equal(0, day.Planned));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_selected_week_inherits_only_its_reported_opening_from_an_older_root(bool explicitOrigin)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await SeedAsync(db);
        var first = await WeekAsync(service, actor, product, new(2026, 9, 7), 100);
        (await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == first.Id)).ExplicitCarryover = explicitOrigin;
        await AddCaptureAsync(db, first.Id, product, actor, ProductionDailyArea.Cutting, 100);
        var root = await db.ProductionScheduleLines.Include(x => x.WorkOrder).ThenInclude(x => x!.Stages).SingleAsync(x => x.WeekId == first.Id);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var capture = await db.ProductionDailyCaptures.SingleAsync(x => x.WeekId == first.Id);
        db.ProductionDailyCaptureAllocations.Add(new() { CaptureId = capture.Id, ScheduleLineId = root.Id, WorkOrderId = root.WorkOrderId!.Value,
            WorkOrderStageId = root.WorkOrder!.Stages.Single(x => x.SourceStageId == config.CuttingStageId).Id, Quantity = 100 });
        await db.SaveChangesAsync();
        var middle = await WeekAsync(service, actor, product, first.WeekStart.AddDays(7), 50);
        (await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == middle.Id)).ExplicitCarryover = false;
        await AddCaptureAsync(db, middle.Id, product, actor, ProductionDailyArea.Sewing, 40);
        var target = await WeekAsync(service, actor, product, middle.WeekStart.AddDays(7), 0, false);
        var options = (await new ProductionWeekOpeningService(db).OptionsAsync(target.Id))
            .Where(x => x.SourceWeekId == middle.Id && x.Area == ProductionDailyArea.Sewing).ToArray();
        Assert.Equal(110, options.Sum(x => x.Available));
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        Assert.Contains(options, x => x.SourceLineId == root.Id && x.Available == 60 && x.OriginalStart == first.WeekStart);
        Assert.True((await service.SaveDraftChangesAsync(new(Guid.NewGuid(), target.Id, target.Version, [], actor,
            options.Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Available, x.Fingerprint)).ToArray()))).Success);
        Assert.Empty(await db.InventoryMovements.ToListAsync());
        var later = await WeekAsync(service, actor, product, target.WeekStart.AddDays(7), 0, false);
        Assert.DoesNotContain(await new ProductionWeekOpeningService(db).OptionsAsync(later.Id),
            x => x.SourceLineId == root.Id && x.Area == ProductionDailyArea.Sewing && x.SourceWeekId == first.Id);
    }

    [Fact]
    public async Task Existing_draft_operation_hash_remains_valid_after_extending_the_review_contract()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var (service, actor, product) = await SeedAsync(db);
        var target = await WeekAsync(service, actor, product, new(2026, 9, 14), 0, false);
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), target.Id, target.Version,
            [new("add", null, null, new(target.WeekStart, product, 10, null, null, null, null))], actor);
        var previousContract = new { command.OperationId, command.WeekId, command.ExpectedWeekVersion,
            command.Changes, command.ActorUserId, command.Openings };
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(previousContract))));
        Assert.True((await service.SaveDraftChangesAsync(command)).Success);
        Assert.Equal(expected, (await db.ProductionScheduleRevisions.SingleAsync(x => x.OperationId == command.OperationId)).RequestFingerprint);
        Assert.True((await service.SaveDraftChangesAsync(command)).Success);
        Assert.Single((await service.GetWeekAsync(target.Id))!.Lines);
    }

    [Fact]
    public async Task Open_copy_requires_review_pin_and_saves_program_and_openings_once()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyOpenAsync(db);
    }

    internal static async Task VerifyOpenAsync(WarehouseDbContext db, bool injectFailure = false)
    {
        var (service, actor, product) = await SeedAsync(db);
        var source = await WeekAsync(service, actor, product, new(2026, 9, 14), 100);
        await AddCaptureAsync(db, source.Id, product, actor, ProductionDailyArea.Cutting, 100);
        var target = await WeekAsync(service, actor, product, source.WeekStart.AddDays(7), 0);
        var options = (await new ProductionWeekOpeningService(db).OptionsAsync(target.Id)).Where(x => x.SourceWeekId == source.Id).ToArray();
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), target.Id, target.Version,
            [new("add", null, null, new(target.WeekStart, product, 50, "NEW", null, null, null))], actor,
            options.Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Available, x.Fingerprint)).ToArray());
        Assert.False((await service.PreviewWorkspaceChangesAsync(command)).CanConfirm);
        command = command with { Reason = "Incorporar pendientes de la semana elegida" };
        var count = await db.ProductionScheduleRevisions.CountAsync();
        var review = await service.PreviewWorkspaceChangesAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        Assert.Equal(count, await db.ProductionScheduleRevisions.CountAsync());
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict, (await service.SaveWorkspaceChangesAsync(command, "4826")).Status);
        command = command with { ReviewedFingerprint = review.Fingerprint };
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await service.SaveWorkspaceChangesAsync(command, "wrong")).Status);
        Assert.Empty(await db.ProductionWeekOpenings.Where(x => x.WeekId == target.Id).ToListAsync());
        if (injectFailure)
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_copy_opening() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Injected opening failure' USING ERRCODE = '23514'; END; $$;
                CREATE TRIGGER reject_copy_opening BEFORE INSERT ON production_week_openings
                FOR EACH ROW EXECUTE FUNCTION reject_copy_opening();
                """);
            try
            {
                Assert.False((await service.SaveWorkspaceChangesAsync(command, "4826")).Success);
                Assert.Empty((await service.GetWeekAsync(target.Id))!.Lines);
                Assert.Empty(await db.ProductionWeekOpenings.Where(x => x.WeekId == target.Id).ToListAsync());
                Assert.Equal(1, await db.ProductionWorkOrders.CountAsync());
                Assert.Equal(count, await db.ProductionScheduleRevisions.CountAsync());
            }
            finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_copy_opening ON production_week_openings; DROP FUNCTION reject_copy_opening();"); }
        }
        Assert.True((await service.SaveWorkspaceChangesAsync(command, "4826")).Success);
        Assert.True((await service.SaveWorkspaceChangesAsync(command, "")).Success);
        Assert.Equal(2, await db.ProductionWeekOpenings.CountAsync());
        Assert.Equal(2, await db.ProductionWorkOrders.CountAsync());
        Assert.Single((await service.GetWeekAsync(target.Id))!.Lines);
        var row = Assert.Single((await new ProductionDailyBalanceService(db).GetDailySummaryAsync(target.Id, new(target.WeekStart)))!.Products);
        Assert.Equal(50, row.Cutting.Pending); Assert.Equal(150, row.Sewing.Pending); Assert.Equal(150, row.ReadyToPack.Pending);
        var audit = await db.ProductionScheduleRevisions.SingleAsync(x => x.OperationId == command.OperationId);
        Assert.Contains(command.Reason, audit.AfterJson); Assert.DoesNotContain("4826", audit.AfterJson);
        target = (await service.GetWeekAsync(target.Id))!;
        options = (await new ProductionWeekOpeningService(db).OptionsAsync(target.Id)).Where(x => x.SourceWeekId == source.Id).ToArray();
        var next = command with { OperationId = Guid.NewGuid(), ExpectedWeekVersion = target.Version, Changes = [],
            Openings = options.Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Selected, x.Fingerprint)).ToArray(), ReviewedFingerprint = "" };
        review = await service.PreviewWorkspaceChangesAsync(next); Assert.True(review.CanConfirm);
        await AddCaptureAsync(db, target.Id, product, actor, ProductionDailyArea.Sewing, 1);
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict,
            (await service.SaveWorkspaceChangesAsync(next with { ReviewedFingerprint = review.Fingerprint }, "4826")).Status);
    }

    internal static ProductionDailyScheduleService Schedule(WarehouseDbContext db)
    {
        var pins = new UserPinService(db, new PinProtector(Key));
        return new(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
    }

    internal static async Task<(ProductionDailyScheduleService Service, Guid Actor, Guid Product)> SeedAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var pins = new UserPinService(db, new PinProtector(Key));
        var actor = new User { FullName = "Copy admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(actor, "4826"); db.Users.Add(actor); await db.SaveChangesAsync();
        return (new(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System),
            actor.Id, (await db.Products.SingleAsync(x => x.Sku == "FG-100")).Id);
    }

    private static async Task<ProductionScheduleWeekView> WeekAsync(ProductionDailyScheduleService service, Guid actor,
        Guid product, DateOnly start, decimal quantity, bool publish = true)
    {
        var id = (await service.CreateWeekAsync(new(Guid.NewGuid(), start, actor))).Id!.Value;
        var week = (await service.GetWeekAsync(id))!;
        if (quantity > 0) Assert.True((await service.SaveLineAsync(new(Guid.NewGuid(), id, null, week.Version, null,
            start, product, quantity, null, null, null, null, actor))).Success);
        week = (await service.GetWeekAsync(id))!;
        if (publish) Assert.True((await service.PublishAsync(new(Guid.NewGuid(), id, week.Version, "4826", actor))).Success);
        return (await service.GetWeekAsync(id))!;
    }

    private static async Task AddCaptureAsync(WarehouseDbContext db, Guid weekId, Guid product, Guid actor,
        ProductionDailyArea area, decimal quantity, bool reversed = false)
    {
        var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == weekId);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        db.ProductionDailyCaptures.Add(new() { WeekId = weekId, ProductId = product, Area = area,
            StageId = (area == ProductionDailyArea.Cutting ? config.CuttingStageId : area == ProductionDailyArea.Sewing ? config.SewingStageId : config.ReadyToPackStageId)!.Value,
            EffectiveDate = week.WeekStart, ShiftId = config.Shift1Id!.Value, ResponsibleUserId = actor,
            Quantity = quantity, Status = reversed ? ProductionDailyCaptureStatus.Reversed : ProductionDailyCaptureStatus.Active,
            OperationId = Guid.NewGuid(), RequestFingerprint = "copy-fixture", IsFlexible = true });
        await db.SaveChangesAsync();
    }
}
