using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionScheduleProgressTests
{
    [Theory]
    [InlineData(false, ProductionScheduleWeekStatus.Draft)]
    [InlineData(false, ProductionScheduleWeekStatus.Open)]
    [InlineData(false, ProductionScheduleWeekStatus.Closed)]
    [InlineData(true, ProductionScheduleWeekStatus.Open)]
    [InlineData(true, ProductionScheduleWeekStatus.Closed)]
    public async Task Adjusted_targets_share_orders_keep_area_backlogs_and_ignore_reversals(bool explicitCarry, ProductionScheduleWeekStatus status)
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await VerifyProjectionAsync(db, explicitCarry, status);
    }

    internal static async Task<Guid> VerifyProjectionAsync(WarehouseDbContext db, bool explicitCarry = true,
        ProductionScheduleWeekStatus status = ProductionScheduleWeekStatus.Open)
    {
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var actor = new User { FullName = "Progress", RoleId = 1, PinLookup = Guid.NewGuid().ToString(), PinHash = "test" };
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek
        {
            CreatedByUser = actor,
            RequestFingerprint = "progress",
            WeekStart = monday,
            WeekEnd = monday.AddDays(6),
            ExplicitCarryover = explicitCarry,
            Status = status
        };
        week.Lines.Add(new() { Product = product, Sequence = 1, PlannedDate = monday, Quantity = 100, OrderReference1 = "A" });
        week.Lines.Add(new() { Product = product, Sequence = 2, PlannedDate = monday, Quantity = 100, OrderReference1 = "B" });
        week.Lines.Add(new() { Product = product, Sequence = 3, PlannedDate = monday.AddDays(1), Quantity = 400 });
        week.Lines.Add(new() { Product = product, Sequence = 4, PlannedDate = monday.AddDays(3), Quantity = 100 });
        week.Lines.Add(new() { Product = product, Sequence = 5, PlannedDate = monday.AddDays(5), Quantity = 200 });
        void Capture(int day, ProductionDailyArea area, decimal quantity, bool shift2 = false, bool reversed = false)
        {
            week.Captures.Add(new()
            {
                Product = product,
                EffectiveDate = monday.AddDays(day),
                Area = area,
                StageId = area switch
                {
                    ProductionDailyArea.Cutting => config.CuttingStageId!.Value,
                    ProductionDailyArea.Sewing => config.SewingStageId!.Value,
                    _ => config.ReadyToPackStageId!.Value
                },
                ShiftId = (shift2 ? config.Shift2Id : config.Shift1Id)!.Value,
                Quantity = quantity,
                ResponsibleUser = actor,
                OperationId = Guid.NewGuid(),
                RequestFingerprint = "progress",
                Status = reversed ? ProductionDailyCaptureStatus.Reversed : ProductionDailyCaptureStatus.Active
            });
        }
        Capture(0, ProductionDailyArea.Cutting, 200); Capture(0, ProductionDailyArea.Cutting, 50, true);
        Capture(0, ProductionDailyArea.Cutting, 25, reversed: true);
        Capture(0, ProductionDailyArea.Sewing, 150);
        Capture(0, ProductionDailyArea.ReadyToPack, 800);
        Capture(1, ProductionDailyArea.Cutting, 350);
        db.Add(week); await db.SaveChangesAsync();
        var result = Assert.Single(await new ProductionDailyBalanceService(db).GetScheduleProgressAsync(week.Id));
        Assert.Equal(7, result.Days.Count);
        var mondayCut = result.Days[0].Areas[0];
        Assert.Equal(200, result.Days[0].Planned); Assert.Equal(250, mondayCut.Produced);
        Assert.Equal(125, mondayCut.Percent); Assert.Equal(-50, mondayCut.Balance);
        Assert.Equal(200, mondayCut.Coverage.Required);
        Assert.Equal(200, mondayCut.Coverage.Covered);
        Assert.Equal(100, mondayCut.Coverage.Percent);
        Assert.Equal(350, result.Days[1].Areas[0].Target); Assert.Equal(100, result.Days[1].Areas[0].Percent);
        Assert.Equal(400, result.Days[1].Areas[0].Coverage.Required);
        Assert.Equal(400, result.Days[1].Areas[0].Coverage.Covered);
        Assert.Equal(0, result.Days[3].Areas[0].Coverage.Covered);
        Assert.Equal(450, result.Days[1].Areas[1].Target);
        Assert.Equal(150, result.Days[0].Areas[1].Coverage.Covered);
        Assert.Equal(75, result.Days[0].Areas[1].Coverage.Percent);
        Assert.Equal(600, result.Days[1].Areas[0].CumulativeProduced);
        Assert.Equal(150, result.Days[1].Areas[1].CumulativeProduced);
        Assert.Equal(0, result.Days[1].Areas[2].Target);
        Assert.Equal(100, result.Days[1].Areas[2].Percent);
        Assert.Equal(400, result.Days[1].Areas[2].AdvanceApplied);
        Assert.Equal(-200, result.Days[2].Areas[2].Balance);
        Assert.Equal(-100, result.Days[3].Areas[2].Balance);
        Assert.Equal(100, result.Days[5].Areas[2].Target);
        Assert.Equal(0, result.Days[2].Areas[0].Target); Assert.False(result.Days[2].Areas[0].CoveredByAdvance);
        Assert.Null(result.Days[2].Areas[0].Coverage.Ratio);
        Assert.Equal(900, week.Lines.Sum(x => x.Quantity));
        Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        return week.Id;
    }

    [Fact]
    public async Task Later_work_recovers_the_oldest_planned_day_without_changing_its_daily_history()
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var actor = new User { FullName = "Recovery", RoleId = 1, PinLookup = "recovery", PinHash = "test" };
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek
        {
            CreatedByUser = actor,
            RequestFingerprint = "recovery",
            WeekStart = monday,
            WeekEnd = monday.AddDays(6),
            Status = ProductionScheduleWeekStatus.Open,
            ExplicitCarryover = true
        };
        foreach (var (day, quantity) in new[] { (0, 200m), (1, 400m), (2, 400m) })
            week.Lines.Add(new()
            {
                Product = product,
                Sequence = day + 1,
                PlannedDate = monday.AddDays(day),
                Quantity = quantity
            });
        ProductionDailyCapture Capture(int day, decimal quantity, bool shift2 = false,
            bool reversed = false) => new()
            {
                Product = product,
                EffectiveDate = monday.AddDays(day),
                Area = ProductionDailyArea.Cutting,
                StageId = config.CuttingStageId!.Value,
                ShiftId = (shift2 ? config.Shift2Id : config.Shift1Id)!.Value,
                Quantity = quantity,
                ResponsibleUser = actor,
                OperationId = Guid.NewGuid(),
                RequestFingerprint = "recovery",
                Status = reversed
                ? ProductionDailyCaptureStatus.Reversed : ProductionDailyCaptureStatus.Active
            };
        week.Captures.Add(Capture(0, 400));
        week.Captures.Add(Capture(1, 200, shift2: true));
        week.Captures.Add(Capture(3, 55));
        week.Captures.Add(Capture(4, 40, shift2: true));
        week.Captures.Add(Capture(4, 500, reversed: true));
        db.Add(week); await db.SaveChangesAsync();
        var service = new ProductionDailyBalanceService(db);
        var before = Assert.Single(await service.GetScheduleProgressAsync(week.Id));
        var wednesday = before.Days[2].Areas[0];
        Assert.Equal(0, wednesday.Produced);
        Assert.Equal(400, wednesday.Coverage.Required);
        Assert.Equal(95, wednesday.Coverage.Covered);
        Assert.Equal(305, wednesday.Coverage.Pending);
        Assert.Equal(23.8m, wednesday.Coverage.Percent);
        Assert.Equal(100, before.Days[0].Areas[0].Coverage.Percent);
        Assert.Equal(100, before.Days[1].Areas[0].Coverage.Percent);
        Assert.Null(before.Days[3].Areas[0].Coverage.Percent);
        Assert.Equal(55, before.Days[3].Areas[0].Produced);
        db.ChangeTracker.Clear();
        db.ProductionDailyCaptures.Add(new()
        {
            WeekId = week.Id,
            ProductId = product.Id,
            ResponsibleUserId = actor.Id,
            EffectiveDate = monday.AddDays(4),
            Area = ProductionDailyArea.Cutting,
            StageId = config.CuttingStageId!.Value,
            ShiftId = config.Shift2Id!.Value,
            Quantity = 305,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "recovery",
            Status = ProductionDailyCaptureStatus.Active
        });
        await db.SaveChangesAsync();
        var after = Assert.Single(await service.GetScheduleProgressAsync(week.Id));
        Assert.Equal(0, after.Days[2].Areas[0].Produced);
        Assert.Equal(wednesday.Balance, after.Days[2].Areas[0].Balance);
        Assert.Equal(400, after.Days[2].Areas[0].Coverage.Covered);
        Assert.Equal(0, after.Days[2].Areas[0].Coverage.Pending);
        Assert.Equal(100, after.Days[2].Areas[0].Coverage.Percent);
    }

    [Fact]
    public void Recovered_coverage_applies_prior_credit_caps_surplus_and_has_no_status_without_a_new_target()
    {
        var monday = ProductionScheduleDayCoverage.Create(100, 0, 0, 50);
        Assert.Equal(50, monday.Covered);
        Assert.Equal(50, monday.Percent);
        Assert.Equal(100, ProductionScheduleDayCoverage.Create(100, 0, 500, 50).Percent);
        Assert.Null(ProductionScheduleDayCoverage.Create(100, 100, 500, 50).Ratio);
        var inherited = ProductionScheduleDayCoverage.Create(230, 0, 100, 0);
        Assert.Equal(230, inherited.Required);
        Assert.Equal(100, inherited.Covered);
        Assert.Equal(130, inherited.Pending);
    }

    [Fact]
    public async Task Accumulated_cut_revalues_earlier_days_without_moving_captures()
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var actor = new User { FullName = "Accumulated", RoleId = 1, PinLookup = "accumulated", PinHash = "test" };
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek
        {
            CreatedByUser = actor,
            RequestFingerprint = "accumulated",
            WeekStart = monday,
            WeekEnd = monday.AddDays(6),
            ExplicitCarryover = true,
            Status = ProductionScheduleWeekStatus.Open
        };
        foreach (var (day, quantity) in new[] { (0, 100m), (1, 200m), (2, 300m) })
            week.Lines.Add(new() { Product = product, Sequence = day + 1, PlannedDate = monday.AddDays(day), Quantity = quantity });
        void Capture(int day, decimal quantity, bool reversed = false) => week.Captures.Add(new()
        {
            Product = product,
            EffectiveDate = monday.AddDays(day),
            Area = ProductionDailyArea.Cutting,
            StageId = config.CuttingStageId!.Value,
            ShiftId = (day == 0 ? config.Shift1Id : config.Shift2Id)!.Value,
            Quantity = quantity,
            ResponsibleUser = actor,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "accumulated",
            Status = reversed ? ProductionDailyCaptureStatus.Reversed : ProductionDailyCaptureStatus.Active
        });
        Capture(0, 200); Capture(1, 100); Capture(1, 25, reversed: true);
        db.Add(week); await db.SaveChangesAsync();
        var row = Assert.Single(await new ProductionDailyBalanceService(db).GetScheduleProgressAsync(week.Id));
        var mondayArea = row.Days[0].Areas[0]; var tuesdayArea = row.Days[1].Areas[0];
        Assert.Equal(200, mondayArea.Percent);
        Assert.Equal(100, tuesdayArea.Percent); // 100 today plus 100 of Monday's advance / 200 planned.
        Assert.Equal(100, tuesdayArea.AdvanceApplied);
        Assert.Equal(100, mondayArea.CumulativeRequirement);
        Assert.Equal(200, row.Days[0].Areas[0].CumulativeProduced);
        Assert.Equal(300, row.Days[1].Areas[0].CumulativeProduced);
        Assert.Equal(3m, (decimal)row.Days[1].Areas[0].CumulativeProduced / mondayArea.CumulativeRequirement);
        Assert.Equal(1m, (decimal)row.Days[1].Areas[0].CumulativeProduced / tuesdayArea.CumulativeRequirement);
        Assert.Equal(600, row.Days[2].Areas[0].CumulativeRequirement);
        var summary = Assert.Single((await new ProductionDailyBalanceService(db).GetDailySummaryAsync(week.Id,
            new ProductionWeeklyFilter(monday.AddDays(1))))!.Products);
        Assert.Equal(1m, summary.Cutting.AccumulatedCoverage.Ratio);
        Assert.Equal(1m, summary.Cutting.DailyCoverage.Ratio);
        using var exported = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db,
            new ProductionDailyBalanceService(db)).ExportAsync(week.Id, new ProductionWeeklyFilter(monday.AddDays(1))))!));
        var sheet = exported.Worksheet("Balance diario");
        Assert.Equal("Status %", sheet.Cell(4, 27).GetString());
        Assert.Equal("Cutting accumulated %", sheet.Cell(4, 28).GetString());
        var exportedRow = Assert.Single(sheet.RowsUsed(), x => x.Cell(1).GetString() == "FG-100");
        Assert.Equal(1m, exportedRow.Cell(28).GetValue<decimal>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_without_a_plan_is_credited_on_the_planned_day(bool explicitCarry)
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var actor = new User { FullName = "Advance", RoleId = 1, PinLookup = Guid.NewGuid().ToString(), PinHash = "test" };
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek
        {
            CreatedByUser = actor,
            RequestFingerprint = "advance",
            WeekStart = monday,
            WeekEnd = monday.AddDays(6),
            ExplicitCarryover = explicitCarry,
            Status = ProductionScheduleWeekStatus.Open
        };
        week.Lines.Add(new() { Product = product, Sequence = 1, PlannedDate = monday.AddDays(3), Quantity = 300 });
        week.Captures.Add(new()
        {
            Product = product,
            EffectiveDate = monday,
            Area = ProductionDailyArea.Cutting,
            StageId = config.CuttingStageId!.Value,
            ShiftId = config.Shift1Id!.Value,
            Quantity = 100,
            ResponsibleUser = actor,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "advance",
            Status = ProductionDailyCaptureStatus.Active
        });
        db.Add(week); await db.SaveChangesAsync();
        var projection = Assert.Single(await new ProductionDailyBalanceService(db).GetScheduleProgressAsync(week.Id));
        Assert.Null(projection.Days[0].Areas[0].Percent);
        Assert.True(projection.Days[1].Areas[0].CoveredByAdvance);
        var thursday = projection.Days[3].Areas[0];
        Assert.Equal(300, thursday.BeforeAdvance);
        Assert.Equal(100, thursday.AdvanceApplied);
        Assert.Equal(200, thursday.Target);
        Assert.Equal(33.3m, thursday.Percent);
        Assert.Equal(200, thursday.Balance);
        Assert.Equal(300, thursday.CumulativeRequirement);
        Assert.Equal(100, thursday.CumulativeProduced);
        Assert.Null(projection.Days[0].Areas[0].Coverage.Ratio);
        Assert.Equal(100, thursday.Coverage.Covered);
        Assert.Equal(33.3m, thursday.Coverage.Percent);
    }

    [Fact]
    public async Task Historical_opening_date_and_omitted_area_are_preserved()
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var route = await db.ProductionRoutes.Include(x => x.Stages).SingleAsync();
        route.Stages = route.Stages.ToList();
        db.ProductionRouteStages.Remove(await db.ProductionRouteStages.SingleAsync(x => x.StageId == config.SewingStageId));
        var product = await db.Products.SingleAsync();
        var week = new ProductionScheduleWeek
        {
            WeekStart = new(2026, 9, 21),
            WeekEnd = new(2026, 9, 27),
            RequestFingerprint = "dated-opening",
            CreatedByUser = new() { FullName = "Opening", RoleId = 1, PinHash = "test", PinLookup = "test" }
        };
        week.Lines.Add(new() { Product = product, PlannedDate = week.WeekStart, Quantity = 600, Sequence = 1 });
        week.Lines.Add(new()
        {
            Product = product,
            PlannedDate = week.WeekStart.AddDays(2),
            Quantity = 7,
            Sequence = 6,
            IsCarryover = true,
            StartArea = ProductionDailyArea.ReadyToPack
        });
        db.Add(week);
        await db.SaveChangesAsync();
        var row = Assert.Single(await new ProductionDailyBalanceService(db).GetScheduleProgressAsync(week.Id));
        Assert.False(row.Days[0].Areas[1].Applies);
        Assert.Null(row.Days[0].Areas[1].Percent);
        Assert.Null(row.Days[0].Areas[1].Coverage.Ratio);
        Assert.Equal(600, row.Days[1].Areas[2].Target);
        Assert.Equal(607, row.Days[2].Areas[2].Target);
        Assert.Equal(600, row.Days[1].Areas[2].CumulativeRequirement);
        Assert.Equal(607, row.Days[2].Areas[2].CumulativeRequirement);
        Assert.Equal(7, row.Days[2].Areas[2].Coverage.Required);
    }

    [Fact]
    public async Task Explicit_admissions_increase_only_their_own_area_target_once()
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        var id = await VerifyProjectionAsync(db);
        var week = await db.ProductionScheduleWeeks.Include(x => x.Lines).SingleAsync(x => x.Id == id);
        var source = new ProductionScheduleWeek
        {
            WeekStart = week.WeekStart.AddDays(-7),
            WeekEnd = week.WeekEnd.AddDays(-7),
            Status = ProductionScheduleWeekStatus.Closed,
            ExplicitCarryover = true,
            CreatedByUserId = week.CreatedByUserId,
            RequestFingerprint = "source"
        };
        var sourceLine = new ProductionScheduleLine
        {
            ProductId = week.Lines.First().ProductId,
            PlannedDate = source.WeekStart,
            Quantity = 30,
            Sequence = 1
        };
        source.Lines.Add(sourceLine); db.Add(source);
        db.ProductionWeekOpenings.Add(new()
        {
            WeekId = id,
            SourceWeekId = source.Id,
            SourceLineId = sourceLine.Id,
            ProductId = sourceLine.ProductId,
            Area = ProductionDailyArea.Cutting,
            Quantity = 30
        });
        await db.SaveChangesAsync();
        var row = Assert.Single(await new ProductionDailyBalanceService(db).GetScheduleProgressAsync(id));
        Assert.Equal(230, row.Days[0].Areas[0].Target); Assert.Equal(380, row.Days[1].Areas[0].Target);
        Assert.Equal(230, row.Days[0].Areas[0].Coverage.Required);
        Assert.Equal(200, row.Days[0].Areas[1].Target); Assert.Equal(450, row.Days[1].Areas[1].Target);
    }

    [Fact]
    public async Task Open_group_checks_pin_versions_closed_state_and_retries_without_duplicate_orders()
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await VerifyOpenAsync(db, false);
    }

    internal static async Task VerifyOpenAsync(WarehouseDbContext db, bool verifyRollback)
    {
        var setup = await ProductionDailyFlexibleTests.SeedAsync(db);
        var service = Schedule(db);
        var week = (await service.GetWeekAsync(setup.Week.Id))!;
        var line = Assert.Single(week.Lines);
        var add = new ProductionScheduleDraftChange("add", null, null,
            new(setup.Date.AddDays(1), setup.Product.Id, 30, "NEW", null, null, null));
        var edit = new ProductionScheduleDraftChange("edit", line.Id, line.Version,
            new(setup.Date, setup.Product.Id, 120, "EDIT", null, null, "Changed"));
        var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), week.Id, week.Version, [edit, add], setup.User.Id);
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin, (await service.SaveWorkspaceChangesAsync(command)).Status);
        Assert.Single((await service.GetWeekAsync(week.Id))!.Lines);
        Assert.True((await service.SaveWorkspaceChangesAsync(command, "4826")).Success);
        Assert.True((await service.SaveWorkspaceChangesAsync(command, "")).Success);
        Assert.Equal(2, (await service.GetWeekAsync(week.Id))!.Lines.Count);
        Assert.Equal(2, await db.ProductionWorkOrders.CountAsync());
        Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict,
            (await service.SaveWorkspaceChangesAsync(command with { OperationId = Guid.NewGuid() }, "4826")).Status);
        week = (await service.GetWeekAsync(week.Id))!;
        var added = week.Lines.Single(x => x.PlannedDate != setup.Date);
        var remove = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), week.Id, week.Version,
            [new("remove", added.Id, added.Version, null)], setup.User.Id);
        Assert.True((await service.SaveWorkspaceChangesAsync(remove)).Success);
        Assert.Single((await service.GetWeekAsync(week.Id))!.Lines);
        if (verifyRollback)
        {
            Assert.True((await ProductionDailyFlexibleTests.Capture(db).ConfirmAsync(new(Guid.NewGuid(), setup.Date,
                ProductionDailyArea.Cutting, setup.Shift, setup.Product.Id, 10, null, "4826"))).Success);
            week = (await service.GetWeekAsync(week.Id))!; line = Assert.Single(week.Lines);
            var failing = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), week.Id, week.Version,
                [add, new("edit", line.Id, line.Version, new(setup.Date, setup.Product.Id, 1, null, null, null, null))], setup.User.Id);
            var orderCount = await db.ProductionWorkOrders.CountAsync();
            Assert.False((await service.SaveWorkspaceChangesAsync(failing, "4826")).Success);
            Assert.Single((await service.GetWeekAsync(week.Id))!.Lines);
            Assert.Equal(orderCount, await db.ProductionWorkOrders.CountAsync());
            Assert.False(await db.ProductionScheduleRevisions.AnyAsync(x => x.OperationId == failing.OperationId));
        }
        week = (await service.GetWeekAsync(week.Id))!;
        Assert.True((await service.CloseAsync(new(Guid.NewGuid(), week.Id, week.Version, setup.User.Id))).Success);
        week = (await service.GetWeekAsync(week.Id))!;
        Assert.False((await service.SaveWorkspaceChangesAsync(new(Guid.NewGuid(), week.Id, week.Version, [add], setup.User.Id), "4826")).Success);
        Assert.DoesNotContain("4826", string.Join("", await db.ProductionScheduleRevisions.Select(x => x.AfterJson).ToArrayAsync()));
    }

    internal static ProductionDailyScheduleService Schedule(WarehouseDbContext db)
    {
        var pins = new UserPinService(db, new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="));
        return new(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
    }
}
