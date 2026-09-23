using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionDailyFlexibleTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Extras_and_out_of_order_captures_reconcile_without_changing_plan()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await db.Database.EnsureCreatedAsync();
        await VerifyAsync(db);
    }

    internal static async Task VerifyAsync(WarehouseDbContext db)
    {
        var setup = await SeedAsync(db);
        var (week, product, user, shift, date) = setup;
        var service = Capture(db);
        await Record(db, setup, ProductionDailyArea.ReadyToPack, 120);
        await Record(db, setup, ProductionDailyArea.Sewing, 130);
        Assert.Empty(await db.ProductionDailyCaptureAllocations.Where(x => x.Capture.ProductId == product.Id).ToListAsync());
        var row = (await new ProductionDailyBalanceService(db).GetAsync(week.Id))!.Rows.Single(x => x.ProductId == product.Id && x.Date == date);
        Assert.Equal(130, row.Sewing.ToReconcile);
        Assert.Equal(10, row.ReadyToPack.Pending);
        Assert.Equal(120, row.ReadyToPack.Completed);
        await Record(db, setup, ProductionDailyArea.Cutting, 100);
        row = (await new ProductionDailyBalanceService(db).GetAsync(week.Id))!.Rows.Single(x => x.ProductId == product.Id && x.Date == date);
        Assert.Equal(30, row.Sewing.ToReconcile);
        var weeklyDifference = Assert.Single((await new ProductionDailyBalanceService(db).GetWeeklyAsync(week.Id, new(date)))!.Products, x => x.ProductId == product.Id);
        Assert.Equal(100, weeklyDifference.Planned);
        Assert.Equal(130, weeklyDifference.Sewing.Completed);
        Assert.Equal(30, weeklyDifference.Sewing.ToReconcile);
        Assert.Equal(10, weeklyDifference.ReadyToPack.Pending);
        Assert.Equal(10, row.ReadyToPack.Pending);
        await Record(db, setup, ProductionDailyArea.Cutting, 50);
        row = (await new ProductionDailyBalanceService(db).GetAsync(week.Id))!.Rows.Single(x => x.ProductId == product.Id && x.Date == date);
        Assert.Equal(0, row.Sewing.ToReconcile);
        Assert.Equal(20, row.Sewing.Pending);
        Assert.Equal(10, row.ReadyToPack.Pending);
        Assert.Equal(50, row.Cutting.Extra);
        Assert.Equal(100, row.NewPlan);
        Assert.Equal(20, (await service.GetAvailabilityAsync(date, ProductionDailyArea.Sewing)).Single(x => x.ProductId == product.Id).Available);
        await Record(db, setup, ProductionDailyArea.Sewing, 20);
        await Record(db, setup, ProductionDailyArea.ReadyToPack, 30);
        var captures = await db.ProductionDailyCaptures.Include(x => x.Allocations).Where(x => x.ProductId == product.Id).ToListAsync();
        Assert.Equal(6, captures.Count);
        Assert.All(captures, capture => Assert.Equal(capture.Quantity, capture.Allocations.Sum(x => x.Quantity)));
        Assert.All(captures.SelectMany(x => x.Allocations), allocation => Assert.NotNull(allocation.ReconciledAt));
        var weeklyFinished = Assert.Single((await new ProductionDailyBalanceService(db).GetWeeklyAsync(week.Id, new(week.WeekEnd)))!.Products, x => x.ProductId == product.Id);
        Assert.Equal(100, weeklyFinished.Planned);
        Assert.Equal(150, weeklyFinished.ReadyToPack.Completed);
        Assert.Equal(50, weeklyFinished.ReadyToPack.Extra);
        Assert.Equal(0, weeklyFinished.Sewing.ToReconcile);
        Assert.Equal(2, await db.ProductionWorkOrders.CountAsync(x => x.ProductId == product.Id));
        Assert.Single((await Schedule(db).GetWeekAsync(week.Id))!.Lines);
        row = (await new ProductionDailyBalanceService(db).GetAsync(week.Id))!.Rows.Single(x => x.ProductId == product.Id && x.Date == date);
        Assert.Equal(150, row.ReadyToPack.Completed);
        Assert.Equal(50, row.ReadyToPack.Extra);
        Assert.Equal(0, row.ReadyToPack.Pending);
        using var book = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, new(db)).ExportAsync(week.Id))!));
        var exported = book.Worksheet(1).Table("AutomaticBalanceExport").DataRange.Rows().First(x => x.Cell(2).GetString() == product.Sku);
        Assert.Equal((double)row.ReadyToPack.Extra, exported.Cell(15).GetDouble());
        Assert.Equal((double)row.Sewing.ToReconcile, exported.Cell(17).GetDouble());
        var extra = await db.ProductionScheduleLines.SingleAsync(x => x.ProductId == product.Id && x.IsExtra);
        Assert.Equal(50, extra.Quantity);
        Assert.Single(await db.ProductionBatches.Where(x => x.WorkOrderId == extra.WorkOrderId).ToListAsync());
    }

    [Fact]
    public async Task Unplanned_product_empty_week_and_unallocated_reversal_are_supported()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await db.Database.EnsureCreatedAsync();
        var setup = await SeedAsync(db, planned: false);
        await Record(db, setup, ProductionDailyArea.ReadyToPack, 12);
        var capture = await db.ProductionDailyCaptures.SingleAsync();
        var reverse = new ReverseProductionDailyCaptureCommand(Guid.NewGuid(), capture.Id, "Correction", "4826");
        Assert.True((await Capture(db).ReverseAsync(reverse)).Success);
        Assert.True((await Capture(db).ReverseAsync(reverse)).Success);
        Assert.Equal(ProductionDailyCommandStatus.IdempotencyConflict, (await Capture(db).ReverseAsync(reverse with { Reason = "Different" })).Status);
        Assert.Empty(await db.ProductionWorkOrders.ToListAsync());
        await Record(db, setup, ProductionDailyArea.Cutting, 25);
        Assert.Empty((await Schedule(db).GetWeekAsync(setup.Week.Id))!.Lines);
        Assert.Single(await db.ProductionWorkOrders.ToListAsync());
        setup.Product.IsActive = false;
        await db.SaveChangesAsync();
        Assert.False((await Capture(db).PreviewAsync(new(setup.Date, ProductionDailyArea.Cutting, setup.Shift, setup.Product.Id, 1))).CanConfirm);
    }

    [Fact]
    public async Task Reconciliation_keeps_effective_dates_and_debt_across_weeks()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await db.Database.EnsureCreatedAsync();
        var setup = await SeedAsync(db);
        await Record(db, setup, ProductionDailyArea.Sewing, 130);
        var schedule = Schedule(db);
        var next = await schedule.CreateWeekAsync(new(Guid.NewGuid(), setup.Date.AddDays(7), setup.User.Id));
        var week = (await schedule.GetWeekAsync(next.Id!.Value))!;
        Assert.True((await schedule.PublishAsync(new(Guid.NewGuid(), week.Id, week.Version, "4826", setup.User.Id))).Success);
        var before = (await new ProductionDailyBalanceService(db).GetAsync(week.Id))!.Rows.First(x => x.ProductId == setup.Product.Id);
        Assert.Equal(130, before.Sewing.ToReconcile);
        Assert.Equal(130, before.ReadyToPack.Pending);
        var later = setup with { Week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == week.Id), Date = week.WeekStart.AddDays(1) };
        await Record(db, later, ProductionDailyArea.Cutting, 130);
        var original = (await new ProductionDailyBalanceService(db).GetAsync(setup.Week.Id))!.Rows.First(x => x.ProductId == setup.Product.Id);
        Assert.Equal(130, original.Sewing.ToReconcile);
        var rows = (await new ProductionDailyBalanceService(db).GetAsync(week.Id))!.Rows.Where(x => x.ProductId == setup.Product.Id).ToArray();
        Assert.Equal(130, rows[0].Sewing.ToReconcile);
        var openingDay = (await new ProductionDailyBalanceService(db).GetDailySummaryAsync(week.Id, new(week.WeekStart)))!
            .Products.Single(x => x.ProductId == setup.Product.Id);
        Assert.Equal(-130, openingDay.Sewing.Opening);
        Assert.Equal(130, openingDay.ReadyToPack.Opening);
        Assert.Equal(100, (await Capture(db).GetAvailabilityAsync(week.WeekStart, ProductionDailyArea.Cutting)).Single(x => x.ProductId == setup.Product.Id).Available);
        Assert.Equal(0, rows[1].Sewing.ToReconcile);
        Assert.Equal(0, rows[1].Sewing.Pending);
        Assert.Equal(130, rows[1].ReadyToPack.Pending);
    }

    [Fact]
    public async Task Operator_can_capture_extras_and_reversal_removes_their_pending_quantity()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await db.Database.EnsureCreatedAsync();
        var setup = await SeedAsync(db, planned: false);
        var pins = new UserPinService(db, new PinProtector(Key));
        var user = new User { FullName = "Flexible operator", RoleId = 2, PinHash = "", PinLookup = "" };
        await pins.AssignAsync(user, "3917"); db.Users.Add(user); await db.SaveChangesAsync();
        var result = await Capture(db).ConfirmAsync(new(Guid.NewGuid(), setup.Date, ProductionDailyArea.Cutting,
            setup.Shift, setup.Product.Id, 25, null, "3917"));
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        var reverse = await Capture(db).ReverseAsync(new(Guid.NewGuid(), result.Id!.Value, "Correction", "4826"));
        Assert.True(reverse.Success, string.Join(" | ", reverse.Errors ?? []));
        Assert.Empty(await Capture(db).GetAvailabilityAsync(setup.Date, ProductionDailyArea.Sewing));
        Assert.Empty(await Capture(db).GetAvailabilityAsync(setup.Date, ProductionDailyArea.Cutting));
        Assert.Empty((await Schedule(db).GetWeekAsync(setup.Week.Id))!.Lines);
    }

    [Fact]
    public async Task Partial_reconciliation_uses_distinct_operations_and_fifo_for_repeated_lines()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await db.Database.EnsureCreatedAsync();
        var setup = await SeedAsync(db);
        var week = (await Schedule(db).GetWeekAsync(setup.Week.Id))!;
        var saved = await Schedule(db).SaveLineAsync(new(Guid.NewGuid(), week.Id, null, week.Version, null,
            setup.Date, setup.Product.Id, 20, null, null, null, null, setup.User.Id, "4826"));
        Assert.True(saved.Success);
        await Record(db, setup, ProductionDailyArea.Sewing, 130);
        await Record(db, setup, ProductionDailyArea.Cutting, 40);
        var sewn = await db.ProductionDailyCaptures.Include(x => x.Allocations).SingleAsync(x => x.Area == ProductionDailyArea.Sewing);
        Assert.Equal(40, sewn.Allocations.Sum(x => x.Quantity));
        await Record(db, setup, ProductionDailyArea.Cutting, 90);
        await db.Entry(sewn).Collection(x => x.Allocations).LoadAsync();
        Assert.Equal(130, sewn.Allocations.Sum(x => x.Quantity));
        var firstLine = await db.ProductionScheduleLines.SingleAsync(x => x.WeekId == week.Id && x.Sequence == 1);
        Assert.Equal(100, sewn.Allocations.Where(x => x.ScheduleLineId == firstLine.Id).Sum(x => x.Quantity));
        Assert.Equal(20, sewn.Allocations.Where(x => x.ScheduleLineId == saved.Id).Sum(x => x.Quantity));
        Assert.Equal(sewn.Allocations.Count, sewn.Allocations.Select(x => x.ProcessOperationId).Distinct().Count());
        var row = (await new ProductionDailyBalanceService(db).GetAsync(week.Id))!.Rows.First();
        Assert.Equal(10, row.Cutting.Extra);
        Assert.Equal(0, row.Sewing.Pending);
        Assert.Equal(0, row.Sewing.ToReconcile);
        Assert.Equal(130, row.ReadyToPack.Pending);
    }

    [Fact]
    public async Task Cutting_a_future_daily_plan_is_an_advance_not_missing_input_or_extra()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await db.Database.EnsureCreatedAsync();
        var setup = await SeedAsync(db);
        var line = await db.ProductionScheduleLines.SingleAsync();
        line.PlannedDate = setup.Date.AddDays(2);
        await db.SaveChangesAsync();
        await Record(db, setup, ProductionDailyArea.Cutting, 20);
        var row = (await new ProductionDailyBalanceService(db).GetAsync(setup.Week.Id))!.Rows.First();
        Assert.Equal(20, row.Cutting.Advance);
        Assert.Equal(0, row.Cutting.ToReconcile);
        Assert.Equal(0, row.Cutting.Extra);
        Assert.Equal(20, row.Sewing.Pending);
    }

    [Fact]
    public async Task Daily_summary_sums_shifts_transfers_two_pieces_and_keeps_signed_debt()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await db.Database.EnsureCreatedAsync();
        var setup = await SeedAsync(db);
        var service = new ProductionDailyBalanceService(db);
        await Record(db, setup, ProductionDailyArea.Cutting, 2);
        var monday = Assert.Single((await service.GetDailySummaryAsync(setup.Week.Id, new(setup.Date)))!.Products);
        Assert.Equal(2, monday.Cutting.Completed);
        Assert.Equal(2, monday.Sewing.Pending);
        var shift2 = (await db.ProductionDailyConfigurations.SingleAsync()).Shift2Id!.Value;
        await Record(db, setup with { Shift = shift2 }, ProductionDailyArea.Cutting, 3);
        await Record(db, setup, ProductionDailyArea.Sewing, 8);
        monday = Assert.Single((await service.GetDailySummaryAsync(setup.Week.Id, new(setup.Date)))!.Products);
        Assert.Equal(5, monday.Cutting.Completed);
        Assert.Equal(8, monday.Sewing.Completed);
        Assert.Equal(3, monday.Sewing.ToReconcile);
        Assert.Equal(98, monday.Cutting.PendingAfterShift1);
        Assert.Equal(-6, monday.Sewing.PendingAfterShift1);
        Assert.Equal(8, monday.ReadyToPack.PendingAfterShift1);
        Assert.Equal(-3, monday.Sewing.NetPending);
        var tuesday = Assert.Single((await service.GetDailySummaryAsync(setup.Week.Id, new(setup.Date.AddDays(1))))!.Products);
        Assert.Equal(-3, tuesday.Sewing.Opening);
        Assert.Equal(0, tuesday.Sewing.Completed);
        Assert.Equal(3, tuesday.Sewing.ToReconcile);
        Assert.Equal(8, tuesday.ReadyToPack.Opening);
        await Record(db, setup with { Date = setup.Date.AddDays(1) }, ProductionDailyArea.Cutting, 100);
        tuesday = Assert.Single((await service.GetDailySummaryAsync(setup.Week.Id, new(setup.Date.AddDays(1))))!.Products);
        Assert.Equal(100, tuesday.Cutting.Completed);
        Assert.Equal(5, tuesday.Cutting.Extra);
        Assert.Equal(0, tuesday.Sewing.ToReconcile);
        Assert.Equal(97, tuesday.Sewing.Pending);
        var wednesday = Assert.Single((await service.GetDailySummaryAsync(setup.Week.Id, new(setup.Date.AddDays(2))))!.Products);
        Assert.Equal(0, wednesday.Cutting.Extra);
        Assert.Equal(0, wednesday.Cutting.Completed);
        Assert.Equal(97, wednesday.Sewing.Opening);
    }

    [Fact]
    public async Task Balance_edits_preview_without_writes_increase_reduce_replace_and_retry()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyBalanceEditsAsync(db);
    }

    internal static async Task VerifyBalanceEditsAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        var setup = await SeedAsync(db);
        await Record(db, setup, ProductionDailyArea.Cutting, 2);
        var service = Capture(db);
        var command = new ProductionBalanceEditCommand(Guid.NewGuid(), setup.Week.Id, setup.Date,
            [new(setup.Product.Id, ProductionDailyArea.Cutting, 1, 2, 5)], Pin: "4826");
        var before = await db.ProductionDailyCaptures.CountAsync();
        var preview = await service.PreviewBalanceEditAsync(command);
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Errors));
        Assert.Equal(before, await db.ProductionDailyCaptures.CountAsync());
        Assert.Equal(5, Assert.Single(preview.Balance!.Products).Cutting.Completed);
        Assert.Equal(5, Assert.Single(preview.Balance.Products).Sewing.Pending);
        command = command with { ReviewedFingerprint = preview.Fingerprint };
        var result = await service.ConfirmBalanceEditAsync(command);
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        Assert.Equal(result.Id, (await service.ConfirmBalanceEditAsync(command)).Id);
        Assert.Equal(before + 1, await db.ProductionDailyCaptures.CountAsync());
        Assert.Equal(ProductionDailyCommandStatus.IdempotencyConflict,
            (await service.ConfirmBalanceEditAsync(command with { Reason = "changed" })).Status);

        command = new(Guid.NewGuid(), setup.Week.Id, setup.Date,
            [new(setup.Product.Id, ProductionDailyArea.Cutting, 1, 5, 1)], Reason: "Correct shift total", Pin: "4826");
        preview = await service.PreviewBalanceEditAsync(command);
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Errors));
        Assert.True(preview.RequiresAdmin);
        var editPins = new UserPinService(db, new PinProtector(Key));
        var editOperator = new User { FullName = "Balance operator", RoleId = 2, PinHash = "", PinLookup = "" };
        await editPins.AssignAsync(editOperator, "3917"); db.Users.Add(editOperator); await db.SaveChangesAsync();
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await service.ConfirmBalanceEditAsync(command with { ReviewedFingerprint = preview.Fingerprint, Pin = "3917" })).Status);
        Assert.False((await service.ConfirmBalanceEditAsync(command with { ReviewedFingerprint = preview.Fingerprint, Reason = null })).Success);
        Assert.Equal(2, Assert.Single(preview.Cells).Reversals.Count);
        Assert.Equal(1, Assert.Single(preview.Balance!.Products).Cutting.Completed);
        result = await service.ConfirmBalanceEditAsync(command with { ReviewedFingerprint = preview.Fingerprint });
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        Assert.Equal(1, await db.ProductionDailyCaptures.Where(x => x.Status == ProductionDailyCaptureStatus.Active).SumAsync(x => x.Quantity));
        Assert.Equal(2, await db.ProductionDailyCaptures.CountAsync(x => x.Status == ProductionDailyCaptureStatus.Reversed));
        Assert.Equal(2, await db.Set<ProductionBalanceEdit>().CountAsync());
        await Record(db, setup, ProductionDailyArea.Cutting, 4);
        Assert.Equal(5, Assert.Single((await new ProductionDailyBalanceService(db).GetDailySummaryAsync(setup.Week.Id, new(setup.Date)))!.Products).Sewing.Pending);
        Assert.True((await service.ConfirmBalanceEditAsync(command with { ReviewedFingerprint = preview.Fingerprint })).Success);
        var zero = new ProductionBalanceEditCommand(Guid.NewGuid(), setup.Week.Id, setup.Date,
            [new(setup.Product.Id, ProductionDailyArea.Cutting, 1, 5, 0)], Reason: "Remove incorrect production", Pin: "4826");
        var zeroReview = await service.PreviewBalanceEditAsync(zero);
        Assert.True(zeroReview.CanConfirm, string.Join(" | ", zeroReview.Errors));
        Assert.True((await service.ConfirmBalanceEditAsync(zero with { ReviewedFingerprint = zeroReview.Fingerprint })).Success);
        Assert.Equal(0, await db.ProductionDailyCaptures.Where(x => x.Status == ProductionDailyCaptureStatus.Active).SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Balance_edits_multiple_areas_match_projection_and_block_dependent_reductions()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyBalanceEditProjectionAsync(db);
    }

    internal static async Task VerifyBalanceEditProjectionAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        var setup = await SeedAsync(db);
        var command = new ProductionBalanceEditCommand(Guid.NewGuid(), setup.Week.Id, setup.Date,
            [new(setup.Product.Id, ProductionDailyArea.Cutting, 1, 0, 150),
             new(setup.Product.Id, ProductionDailyArea.Sewing, 1, 0, 130),
             new(setup.Product.Id, ProductionDailyArea.ReadyToPack, 2, 0, 125)], Pin: "4826");
        var service = Capture(db);
        var preview = await service.PreviewBalanceEditAsync(command);
        Assert.True(preview.CanConfirm, string.Join(" | ", preview.Errors));
        var result = await service.ConfirmBalanceEditAsync(command with { ReviewedFingerprint = preview.Fingerprint });
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        var actual = Assert.Single((await new ProductionDailyBalanceService(db).GetDailySummaryAsync(setup.Week.Id, new(setup.Date)))!.Products);
        var projected = Assert.Single(preview.Balance!.Products);
        Assert.Equal(projected.Cutting, actual.Cutting);
        Assert.Equal(projected.Sewing, actual.Sewing);
        Assert.Equal(projected.ReadyToPack, actual.ReadyToPack);
        var blocked = await service.PreviewBalanceEditAsync(new(Guid.NewGuid(), setup.Week.Id, setup.Date,
            [new(setup.Product.Id, ProductionDailyArea.Cutting, 1, 150, 100)], Reason: "Correction"));
        Assert.False(blocked.CanConfirm);
        Assert.Contains(blocked.Errors, x => x.Contains("dependiente"));
        var stale = await service.PreviewBalanceEditAsync(command with { OperationId = Guid.NewGuid() });
        Assert.False(stale.CanConfirm);
    }

    private sealed record Setup(ProductionScheduleWeek Week, Product Product, User User, Guid Shift, DateOnly Date);
    private static async Task<Setup> SeedAsync(WarehouseDbContext db, bool planned = true)
    {
        var pins = new UserPinService(db, new PinProtector(Key));
        var user = await pins.AuthenticateAsync("4826");
        if (user is null)
        {
            user = new User { FullName = "Flexible admin", RoleId = 1, PinHash = "", PinLookup = "" };
            await pins.AssignAsync(user, "4826"); db.Users.Add(user);
        }
        if ((await db.ProductionDailyConfigurations.SingleAsync()).CuttingStageId is null)
            await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var product = new Product { Sku = "FLEX-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), BaseUnitId = 1 };
        db.Products.Add(product); await db.SaveChangesAsync();
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-70);
        date = date.AddDays(-((int)date.DayOfWeek + 6) % 7);
        var schedule = Schedule(db);
        var created = await schedule.CreateWeekAsync(new(Guid.NewGuid(), date, user.Id));
        Assert.True(created.Success);
        var week = (await schedule.GetWeekAsync(created.Id!.Value))!;
        if (planned) Assert.True((await schedule.SaveLineAsync(new(Guid.NewGuid(), week.Id, null, week.Version, null, date, product.Id, 100, null, null, null, null, user.Id))).Success);
        week = (await schedule.GetWeekAsync(week.Id))!;
        var published = await schedule.PublishAsync(new(Guid.NewGuid(), week.Id, week.Version, "4826", user.Id));
        Assert.True(published.Success, string.Join(" | ", published.Errors ?? []));
        return new(await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == week.Id), product, user,
            (await db.ProductionDailyConfigurations.SingleAsync()).Shift1Id!.Value, date);
    }
    private static async Task Record(WarehouseDbContext db, Setup setup, ProductionDailyArea area, decimal quantity)
    {
        var service = Capture(db);
        var command = new ProductionCaptureGroupCommand(Guid.NewGuid(), setup.Date, area, setup.Shift, [new(setup.Product.Id, quantity, null)], Pin: "4826");
        var review = await service.PreviewGroupAsync(command);
        Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        command = command with { ReviewedFingerprint = review.Fingerprint };
        var result = await service.ConfirmGroupAsync(command);
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        Assert.True((await service.ConfirmGroupAsync(command)).Success);
    }
    private static ProductionDailyScheduleService Schedule(WarehouseDbContext db)
    {
        var pins = new UserPinService(db, new PinProtector(Key));
        return new(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
    }
    private static ProductionDailyCaptureService Capture(WarehouseDbContext db)
    {
        var pins = new UserPinService(db, new PinProtector(Key));
        return new(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), new WarehouseClock(new WarehouseSettingsService(db)), TimeProvider.System);
    }
}
