using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionExplicitCarryoverTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Area_openings_shifts_advances_extras_and_reversals_keep_one_week_of_work()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var admin = await ProductionOpeningImportTests.Seed(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var monday = new DateOnly(2026, 10, 5);
        var source = new ProductionScheduleWeek { WeekStart = monday.AddDays(-7), WeekEnd = monday.AddDays(-1),
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('A', 64), CreatedByUserId = admin };
        var root = new ProductionScheduleLine { Week = source, ProductId = product.Id, Quantity = 152, PlannedDate = source.WeekStart };
        var week = new ProductionScheduleWeek { WeekStart = monday, WeekEnd = monday.AddDays(6), ExplicitCarryover = true,
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('B', 64), CreatedByUserId = admin, Status = ProductionScheduleWeekStatus.Open };
        week.Lines.Add(new() { ProductId = product.Id, Quantity = 100, PlannedDate = monday });
        week.Lines.Add(new() { ProductId = product.Id, Quantity = 50, PlannedDate = monday.AddDays(1) });
        void Capture(ProductionDailyArea area, Guid stage, int day, Guid shift, decimal qty, bool reversed = false) => week.Captures.Add(new() {
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('C', 64), ProductId = product.Id, StageId = stage,
            Area = area, EffectiveDate = monday.AddDays(day), ShiftId = shift, Quantity = qty, ResponsibleUserId = admin,
            Status = reversed ? ProductionDailyCaptureStatus.Reversed : ProductionDailyCaptureStatus.Active });
        Capture(ProductionDailyArea.Cutting, config.CuttingStageId!.Value, 0, config.Shift1Id!.Value, 120);
        Capture(ProductionDailyArea.Cutting, config.CuttingStageId.Value, 0, config.Shift2Id!.Value, 10);
        Capture(ProductionDailyArea.Cutting, config.CuttingStageId.Value, 0, config.Shift1Id.Value, 7, true);
        Capture(ProductionDailyArea.Cutting, config.CuttingStageId.Value, 6, config.Shift1Id.Value, 90);
        Capture(ProductionDailyArea.Sewing, config.SewingStageId!.Value, 0, config.Shift1Id.Value, 20);
        Capture(ProductionDailyArea.Sewing, config.SewingStageId.Value, 0, config.Shift2Id.Value, 5);
        db.AddRange(source, root, week);
        db.ProductionWeekOpenings.Add(new() { WeekId = week.Id, SourceWeekId = source.Id, SourceLineId = root.Id,
            ProductId = product.Id, Area = ProductionDailyArea.Sewing, Quantity = 40 });
        await db.SaveChangesAsync();
        var balances = new ProductionDailyBalanceService(db);
        var day = Assert.Single((await balances.GetDailySummaryAsync(week.Id, new(monday)))!.Products);
        Assert.Equal(-20, day.Cutting.PendingAfterShift1); Assert.Equal(-30, day.Cutting.NetPending);
        Assert.Equal(30, day.Cutting.Advance); Assert.Equal(0, day.Cutting.Extra);
        Assert.Equal(40, day.Sewing.Opening); Assert.Equal(120, day.Sewing.PendingAfterShift1); Assert.Equal(115, day.Sewing.Pending);
        Assert.Equal(100, day.ReadyToPack.Pending);
        var next = Assert.Single((await balances.GetDailySummaryAsync(week.Id, new(monday.AddDays(1))))!.Products);
        Assert.Equal(-30, next.Cutting.Opening); Assert.Equal(20, next.Cutting.Pending);
        Assert.Equal(115, next.Sewing.Opening); Assert.Equal(165, next.Sewing.Pending);
        var sunday = Assert.Single((await balances.GetDailySummaryAsync(week.Id, new(week.WeekEnd)))!.Products);
        Assert.Equal(-70, sunday.Cutting.NetPending); Assert.Equal(70, sunday.Cutting.Extra);
        Assert.Equal(165, sunday.Sewing.Pending);
    }

    [Fact]
    public async Task Explicit_opening_is_partial_transactional_and_separate_from_physical_supply()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyAsync(db);
    }

    internal static async Task VerifyAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var pins = new UserPinService(db, new PinProtector(Key));
        var admin = new User { FullName = "Carryover admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "4826"); db.Add(admin); await db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
        var capture = new ProductionDailyCaptureService(db, pins, new InventoryMovementService(db, pins, TimeProvider.System),
            new WarehouseClock(new WarehouseSettingsService(db)), TimeProvider.System);
        var balances = new ProductionDailyBalanceService(db);
        var openings = new ProductionWeekOpeningService(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var monday = new DateOnly(2026, 9, 21);
        async Task<ProductionScheduleWeekView> Create(DateOnly date, decimal quantity)
        {
            var result = await service.CreateWeekAsync(new(Guid.NewGuid(), date, admin.Id)); Assert.True(result.Success);
            var week = (await service.GetWeekAsync(result.Id!.Value))!;
            Assert.True(week.ExplicitCarryover);
            if (quantity > 0) Assert.True((await service.SaveLineAsync(new(Guid.NewGuid(), week.Id, null, week.Version, null, date,
                product.Id, quantity, null, null, null, null, admin.Id))).Success);
            return (await service.GetWeekAsync(week.Id))!;
        }
        async Task Publish(ProductionScheduleWeekView week)
        {
            var current = (await service.GetWeekAsync(week.Id))!;
            var result = await service.PublishAsync(new(Guid.NewGuid(), week.Id, current.Version, "4826", admin.Id));
            Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        }
        var source = await Create(monday.AddDays(-7), 152); await Publish(source);
        var target = await Create(monday, 100);
        var empty = Assert.Single((await balances.GetDailySummaryAsync(target.Id, new(monday)))!.Products);
        Assert.Equal(0, empty.Cutting.Opening);
        Assert.Equal(100, empty.Cutting.Pending); Assert.Equal(100, empty.Sewing.Pending); Assert.Equal(100, empty.ReadyToPack.Pending);
        var option = Assert.Single((await openings.OptionsAsync(target.Id)), x => x.Area == ProductionDailyArea.Cutting);
        Assert.Equal(152, option.Available); Assert.Equal(0, option.Selected); Assert.True(option.Provisional);
        ProductionOpeningChange Change(decimal value) => new(option.SourceWeekId, option.SourceLineId, option.Area, value, option.Fingerprint);
        var save = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), target.Id, target.Version, [], admin.Id, [Change(40)]);
        Assert.False((await service.SaveDraftChangesAsync(save with { Openings = [Change(153)] })).Success);
        Assert.Empty(await db.ProductionWeekOpenings.ToListAsync());
        Assert.True((await service.SaveDraftChangesAsync(save)).Success);
        Assert.True((await service.SaveDraftChangesAsync(save)).Success);
        Assert.Single(await db.ProductionWeekOpenings.ToListAsync());
        Assert.Equal(2, await db.ProductionScheduleLines.CountAsync());
        var daily = Assert.Single((await balances.GetDailySummaryAsync(target.Id, new(monday)))!.Products);
        Assert.Equal(40, daily.Cutting.Opening); Assert.Equal(140, daily.Cutting.Pending); Assert.Equal(100, daily.Sewing.Pending);
        await Publish(target);
        var newWeek = await Create(monday.AddDays(7), 0);
        Assert.Empty((await balances.GetDailySummaryAsync(newWeek.Id, new(monday.AddDays(7))))!.Products);
        var remaining = Assert.Single(await openings.OptionsAsync(newWeek.Id), x => x.SourceWeekId == source.Id && x.Area == ProductionDailyArea.Cutting);
        Assert.Equal(112, remaining.Available);
        // Capture the admitted root first, then the target's own program. Retry must not add production.
        var command = new ProductionCaptureGroupCommand(Guid.NewGuid(), monday, ProductionDailyArea.Cutting, config.Shift1Id!.Value,
            [new(product.Id, 50, null)], Pin: "4826");
        var review = await capture.PreviewGroupAsync(command); Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
        command = command with { ReviewedFingerprint = review.Fingerprint };
        var confirmed = await capture.ConfirmGroupAsync(command); Assert.True(confirmed.Success, string.Join(" | ", confirmed.Errors ?? []));
        Assert.True((await capture.ConfirmGroupAsync(command)).Success);
        Assert.Equal(50, await db.ProductionDailyCaptures.Where(x => x.WeekId == target.Id).SumAsync(x => x.Quantity));
        daily = Assert.Single((await balances.GetDailySummaryAsync(target.Id, new(monday)))!.Products);
        Assert.Equal(90, daily.Cutting.PendingAfterShift1); Assert.Equal(90, daily.Cutting.Pending);
        Assert.Equal(100, daily.Sewing.Pending); // Work pending is independent of upstream physical receipts.
        var tuesday = Assert.Single((await balances.GetDailySummaryAsync(target.Id, new(monday.AddDays(1))))!.Products);
        Assert.Equal(90, tuesday.Cutting.Opening); Assert.Equal(90, tuesday.Cutting.Pending);
        var current = (await service.GetWeekAsync(target.Id))!;
        var latest = Assert.Single(await openings.OptionsAsync(target.Id), x => x.Area == ProductionDailyArea.Cutting);
        var reduce = new ProductionOpeningCorrection(Guid.NewGuid(), target.Id, current.Version,
            [new(latest.SourceWeekId, latest.SourceLineId, latest.Area, 0, latest.Fingerprint)], admin.Id, "Retirar arrastre");
        Assert.False((await service.PreviewOpeningCorrectionAsync(reduce)).CanConfirm);
        var increase = reduce with { Changes = [reduce.Changes[0] with { Quantity = 45 }] };
        var correctionReview = await service.PreviewOpeningCorrectionAsync(increase); Assert.True(correctionReview.CanConfirm);
        increase = increase with { Fingerprint = correctionReview.Fingerprint, Pin = "4826" };
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await service.ConfirmOpeningCorrectionAsync(increase with { Pin = "0000" })).Status);
        Assert.True((await service.ConfirmOpeningCorrectionAsync(increase)).Success);
        Assert.True((await service.ConfirmOpeningCorrectionAsync(increase)).Success);
        Assert.Single(await db.ProductionScheduleRevisions.Where(x => x.Action == "opening-corrected").ToListAsync());
        var close = Assert.Single((await balances.GetWeekCloseAsync(target.Id, new(monday)))!.Products);
        Assert.Equal(95, close.Cutting.Pending); Assert.Equal(100, close.Sewing.Pending);
        using var exported = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, balances).ExportAsync(target.Id))!));
        Assert.Equal(45, exported.Worksheet("Arrastre inicial").Cell(4, 4).GetValue<decimal>());
        Assert.Equal(95, exported.Worksheet("Balance diario").Cell(5, 5).GetValue<decimal>());
        // The balance editor must preview physical residuals with the same admission rules as confirmation.
        var edit = new ProductionBalanceEditCommand(Guid.NewGuid(), target.Id, monday,
            [new(product.Id, ProductionDailyArea.Sewing, 1, 0, 120)], Pin: "4826", AdminActorId: admin.Id);
        var editReview = await capture.PreviewBalanceEditAsync(edit);
        Assert.True(editReview.CanConfirm, string.Join(" | ", editReview.Errors));
        Assert.Equal(-20, Assert.Single(editReview.Balance!.Products).Sewing.NetPending);
        var editResult = await capture.ConfirmBalanceEditAsync(edit with { ReviewedFingerprint = editReview.Fingerprint });
        Assert.True(editResult.Success, string.Join(" | ", editResult.Errors ?? []));
        var actual = Assert.Single((await balances.GetDailySummaryAsync(target.Id, new(monday)))!.Products);
        Assert.Equal(Assert.Single(editReview.Balance.Products).Sewing, actual.Sewing);
        Assert.True(actual.Sewing.ToReconcile > 0);
        // Count each admission together with ordinary daily rows. 101 must leave everything untouched.
        var nextOption = Assert.Single(await openings.OptionsAsync(newWeek.Id), x => x.SourceWeekId == source.Id && x.Area == ProductionDailyArea.Cutting);
        var one = new ProductionOpeningChange(nextOption.SourceWeekId, nextOption.SourceLineId, nextOption.Area, 1, nextOption.Fingerprint);
        var add = new ProductionScheduleDraftChange("add", null, null, new(newWeek.WeekStart, product.Id, 1, null, null, null, null));
        var max = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), newWeek.Id, newWeek.Version, Enumerable.Repeat(add, 100).ToArray(), admin.Id, [one]);
        Assert.False((await service.SaveDraftChangesAsync(max)).Success);
        Assert.Empty(await db.ProductionScheduleLines.Where(x => x.WeekId == newWeek.Id).ToListAsync());
        max = max with { Changes = Enumerable.Repeat(add, 99).ToArray() };
        if (db.Database.IsNpgsql())
        {
            Assert.StartsWith("warehouse_epi_balance_test_", db.Database.GetDbConnection().Database);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_test_opening() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Injected opening failure' USING ERRCODE = '23514'; END; $$;
                CREATE TRIGGER reject_test_opening BEFORE INSERT ON production_week_openings
                FOR EACH ROW EXECUTE FUNCTION reject_test_opening();
                """);
            try
            {
                Assert.False((await service.SaveDraftChangesAsync(max)).Success);
                Assert.Empty(await db.ProductionScheduleLines.Where(x => x.WeekId == newWeek.Id).ToListAsync());
                Assert.Empty(await db.ProductionWeekOpenings.Where(x => x.WeekId == newWeek.Id).ToListAsync());
                Assert.False(await db.ProductionScheduleRevisions.AnyAsync(x => x.OperationId == max.OperationId));
            }
            finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_test_opening ON production_week_openings; DROP FUNCTION reject_test_opening();"); }
        }
        Assert.True((await service.SaveDraftChangesAsync(max)).Success);
        Assert.Equal(99, await db.ProductionScheduleLines.CountAsync(x => x.WeekId == newWeek.Id));
        // Source changes require a fresh review; no silent resizing/replacement.
        var currentSource = (await service.GetWeekAsync(source.Id))!;
        var sourceLine = Assert.Single(currentSource.Lines);
        Assert.True((await service.SaveLineAsync(new(Guid.NewGuid(), source.Id, sourceLine.Id, currentSource.Version,
            sourceLine.Version, sourceLine.PlannedDate, product.Id, 153, null, null, null, "Origen revisado", admin.Id, "4826"))).Success);
        Assert.NotEmpty(await openings.RevalidateAsync(target.Id));
        Assert.NotEmpty(await openings.RevalidateAsync(newWeek.Id));
        var nextCurrent = (await service.GetWeekAsync(newWeek.Id))!;
        Assert.False((await service.PublishAsync(new(Guid.NewGuid(), newWeek.Id, nextCurrent.Version, "4826", admin.Id))).Success);
    }

    internal static async Task VerifyMigrationAsync(WarehouseDbContext db)
    {
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var admin = new User { FullName = "Migration admin", RoleId = 1, PinLookup = "migration", PinHash = "migration" };
        db.Add(admin); await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var dates = Enumerable.Range(0, 4).Select(i => new DateOnly(2026, 8, 3).AddDays(7 * i)).ToArray();
        var weeks = dates.Select(date => new ProductionScheduleWeek { OperationId = Guid.NewGuid(), RequestFingerprint = new string('M', 64),
            WeekStart = date, WeekEnd = date.AddDays(6), CreatedByUserId = admin.Id }).ToArray();
        weeks[1].Status = ProductionScheduleWeekStatus.Closed; weeks[2].Origin = ProductionScheduleOrigin.ExcelImport;
        weeks[3].Captures.Add(new() { OperationId = Guid.NewGuid(), RequestFingerprint = new string('C', 64), ProductId = product.Id,
            EffectiveDate = weeks[3].WeekStart, StageId = config.CuttingStageId!.Value, ShiftId = config.Shift1Id!.Value,
            Area = ProductionDailyArea.Cutting, Quantity = 1, ResponsibleUserId = admin.Id, Status = ProductionDailyCaptureStatus.Reversed });
        db.AddRange(weeks); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync("20260923150000_SevenDayProductionSchedule");
        await db.Database.MigrateAsync();
        var migrated = await db.ProductionScheduleWeeks.OrderBy(x => x.WeekStart).ToListAsync();
        Assert.True(migrated[0].ExplicitCarryover);
        Assert.All(migrated.Skip(1), week => Assert.False(week.ExplicitCarryover));
        Assert.Empty(await db.ProductionWeekOpenings.ToListAsync());
    }
}
