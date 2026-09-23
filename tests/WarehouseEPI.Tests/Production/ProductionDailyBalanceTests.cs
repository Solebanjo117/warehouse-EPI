using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionDailyBalanceTests
{
    private const string PinKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Mixed_routes_keep_carryover_visible_and_send_each_capture_to_its_actual_next_stage()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyMixedRoutesAsync(db);
    }

    [Fact]
    public async Task Mixed_routes_balance_and_export_work_on_isolated_postgresql()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var database = "warehouse_epi_balance_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_balance_test_[a-f0-9]{32}$", database);
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var testBuilder = new NpgsqlConnectionStringBuilder(source) { Database = database, Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseNpgsql(testBuilder.ConnectionString).Options);
            await VerifyMixedRoutesAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyMixedRoutesAsync(WarehouseDbContext db)
    {
        var setup = await SeedAsync(db);
        var balance = new ProductionDailyBalanceService(db);
        var draft = Monday((await balance.GetAsync(setup.Week.Id))!);
        Assert.Equal(5, draft.Cutting.Pending);
        Assert.True(draft.Sewing.Applies);
        Assert.Equal(3, draft.Sewing.Pending);
        await PublishAsync(db, setup);
        var published = Monday((await balance.GetAsync(setup.Week.Id))!);
        Assert.Equal(draft, published with { References = draft.References });

        await CaptureAsync(db, setup, ProductionDailyArea.Cutting, 2);
        var partial = Monday((await balance.GetAsync(setup.Week.Id))!);
        Assert.Equal(3, partial.Cutting.Pending);
        Assert.Equal(3, partial.Sewing.Pending);
        Assert.Equal(2, partial.ReadyToPack.Pending);
        await CaptureAsync(db, setup, ProductionDailyArea.Cutting, 3);
        var cut = Monday((await balance.GetAsync(setup.Week.Id))!);
        Assert.Equal(0, cut.Cutting.Pending);
        Assert.Equal(3, cut.Sewing.Pending);
        Assert.Equal(5, cut.ReadyToPack.Pending);
        await CaptureAsync(db, setup, ProductionDailyArea.Sewing, 3);
        var sewn = Monday((await balance.GetAsync(setup.Week.Id))!);
        Assert.Equal(0, sewn.Sewing.Pending);
        Assert.Equal(8, sewn.ReadyToPack.Pending);

        var finalCapture = await CaptureAsync(db, setup, ProductionDailyArea.ReadyToPack, 6);
        Assert.Equal(2, await db.ProductionDailyCaptureAllocations.CountAsync(x => x.CaptureId == finalCapture));
        var finished = Monday((await balance.GetAsync(setup.Week.Id))!);
        Assert.Equal(2, finished.ReadyToPack.Pending);
        Assert.Equal(75, finished.ProgressPercent);
        var reversed = await Captures(db).ReverseAsync(new(Guid.NewGuid(), finalCapture, "Corrección", "4826"));
        Assert.True(reversed.Success, string.Join(" | ", reversed.Errors ?? []));
        var restored = Monday((await balance.GetAsync(setup.Week.Id))!);
        Assert.Equal(8, restored.ReadyToPack.Pending);
        Assert.Equal(0, restored.ReadyToPack.Completed);
        Assert.Equal(0, restored.ProgressPercent);
        var summary = Assert.Single((await balance.GetWeeklyAsync(setup.Week.Id, new(setup.Week.WeekEnd)))!.Products);
        Assert.Equal(5, summary.Planned);
        Assert.Equal(5, summary.Cutting.Completed);
        Assert.Equal(3, summary.Sewing.Opening);
        Assert.Equal(3, summary.Sewing.Completed);
        Assert.Equal(8, summary.ReadyToPack.Pending);
        Assert.Equal(0, summary.ReadyToPack.Completed);

        using var workbook = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, balance).ExportAsync(setup.Week.Id))!));
        var exported = workbook.Worksheet(1).Table("AutomaticBalanceExport").DataRange.FirstRow();
        Assert.Equal((double)restored.Sewing.Pending, exported.Cell(8).GetDouble());
        Assert.Equal((double)restored.ReadyToPack.Pending, exported.Cell(10).GetDouble());
        Assert.Equal((double)restored.ReadyToPack.Completed, exported.Cell(9).GetDouble());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_snapshot_survives_route_edit_or_deactivation(bool deactivate)
    {
        await using var db = ProductionOpeningImportTests.Context();
        var setup = await SeedAsync(db);
        await PublishAsync(db, setup);
        await CaptureAsync(db, setup, ProductionDailyArea.Cutting, 5);
        var before = Monday((await new ProductionDailyBalanceService(db).GetAsync(setup.Week.Id))!);
        var route = await db.ProductionRoutes.Include(x => x.Stages).SingleAsync();
        if (deactivate) route.IsActive = false;
        else
        {
            route.Stages.Single(x => x.StageId == setup.Config.ReadyToPackStageId).Sequence = 3;
            var stage = new ProductionRouteStage { RouteId = route.Id, StageId = setup.Config.SewingStageId!.Value, Sequence = 2 };
            route.Stages.Add(stage);
            db.Entry(stage).State = EntityState.Added;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var after = Monday((await new ProductionDailyBalanceService(db).GetAsync(setup.Week.Id))!);
        Assert.Equal(before, after with { References = before.References });
    }

    [Fact]
    public async Task Prior_week_pending_uses_allocations_and_effective_dates_without_counting_imported_history_twice()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var setup = await SeedAsync(db);
        await PublishAsync(db, setup);
        await CaptureAsync(db, setup, ProductionDailyArea.Cutting, 5);
        await CaptureAsync(db, setup, ProductionDailyArea.Sewing, 3);
        var prior = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == setup.Week.Id);
        prior.Status = ProductionScheduleWeekStatus.Closed;
        var next = new ProductionScheduleWeek
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('N', 64),
            WeekStart = prior.WeekStart.AddDays(7),
            WeekEnd = prior.WeekEnd.AddDays(7),
            Status = ProductionScheduleWeekStatus.Open,
            CreatedByUserId = setup.Actor,
            CreatedAt = DateTimeOffset.UtcNow
        };
        // Excel history has no allocations and must not feed the live orders' opening again.
        var imported = new ProductionScheduleWeek
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('H', 64),
            WeekStart = prior.WeekStart.AddDays(-7),
            WeekEnd = prior.WeekEnd.AddDays(-7),
            Status = ProductionScheduleWeekStatus.Closed,
            CreatedByUserId = setup.Actor,
            CreatedAt = DateTimeOffset.UtcNow,
            Origin = ProductionScheduleOrigin.ExcelImport
        };
        imported.Captures.Add(new ProductionDailyCapture
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('H', 64),
            ProductId = setup.Product,
            Area = ProductionDailyArea.Sewing,
            StageId = setup.Config.SewingStageId!.Value,
            ShiftId = setup.Config.Shift1Id!.Value,
            EffectiveDate = imported.WeekStart,
            Quantity = 100,
            ResponsibleUserId = setup.Actor,
            Origin = ProductionScheduleOrigin.ExcelImport
        });
        db.AddRange(next, imported);
        await db.SaveChangesAsync();
        var currentSetup = setup with { Week = next };
        var capture = await CaptureAsync(db, currentSetup, ProductionDailyArea.ReadyToPack, 6, next.WeekStart.AddDays(1));
        var rows = (await new ProductionDailyBalanceService(db).GetAsync(next.Id))!.Rows;
        var weekly = Assert.Single((await new ProductionDailyBalanceService(db).GetWeeklyAsync(next.Id, new(next.WeekEnd)))!.Products);
        Assert.Equal(8, weekly.ReadyToPack.Opening);
        Assert.Equal(6, weekly.ReadyToPack.Completed);
        Assert.Equal(2, weekly.ReadyToPack.Pending);
        var suggested = await Captures(db).SearchDailyProductsAsync(next.WeekStart, ProductionDailyArea.ReadyToPack, null);
        Assert.Equal(setup.Product, Assert.Single(suggested.Single(x => x.Group == 1).Items).Id);
        Assert.Equal(8, rows[0].ReadyToPack.Pending);
        Assert.Equal(0, rows[0].ReadyToPack.Completed);
        Assert.Equal(2, rows[1].ReadyToPack.Pending);
        Assert.Equal(6, rows[1].ReadyToPack.Completed);
        Assert.Equal(0, rows[1].ReadyToPack.Advance);
        Assert.Equal(75, rows[1].ProgressPercent);
        // A capture in the following week cannot change the prior week's closing.
        Assert.Equal(8, Monday((await new ProductionDailyBalanceService(db).GetAsync(prior.Id))!).ReadyToPack.Pending);
        var reversed = await Captures(db).ReverseAsync(new(Guid.NewGuid(), capture, "Corrección", "4826"));
        Assert.True(reversed.Success, string.Join(" | ", reversed.Errors ?? []));
        Assert.All((await new ProductionDailyBalanceService(db).GetAsync(next.Id))!.Rows, row => Assert.Equal(8, row.ReadyToPack.Pending));
    }

    [Fact]
    public async Task Unallocated_historical_captures_remain_separate_from_live_orders_in_the_same_week()
    {
        await using var db = ProductionOpeningImportTests.Context();
        var setup = await SeedAsync(db);
        await PublishAsync(db, setup);
        await CaptureAsync(db, setup, ProductionDailyArea.Cutting, 5);
        db.ProductionScheduleLines.Add(new ProductionScheduleLine
        {
            WeekId = setup.Week.Id,
            ProductId = setup.Product,
            Sequence = 3,
            Quantity = 4,
            PlannedDate = setup.Week.WeekStart,
            Origin = ProductionScheduleOrigin.ExcelImport
        });
        db.ProductionDailyCaptures.Add(new ProductionDailyCapture
        {
            WeekId = setup.Week.Id,
            ProductId = setup.Product,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('L', 64),
            Area = ProductionDailyArea.Cutting,
            StageId = setup.Config.CuttingStageId!.Value,
            ShiftId = setup.Config.Shift1Id!.Value,
            EffectiveDate = setup.Week.WeekStart,
            Quantity = 4,
            ResponsibleUserId = setup.Actor,
            Origin = ProductionScheduleOrigin.ExcelImport
        });
        await db.SaveChangesAsync();
        var row = Monday((await new ProductionDailyBalanceService(db).GetAsync(setup.Week.Id))!);
        Assert.Equal(9, row.Cutting.Completed);
        Assert.Equal(0, row.Cutting.Pending);
        Assert.Equal(3, row.Sewing.Pending);
        Assert.Equal(9, row.ReadyToPack.Pending);
        Assert.Equal(9, row.Shift1Completed);
    }

    private static ProductionDailyBalanceRow Monday(ProductionDailyBalanceView view) => Assert.Single(view.Rows, x => x.Date == view.WeekStart);

    private static async Task<Setup> SeedAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var actor = new User { FullName = "Balance admin", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(actor, "4826");
        db.Add(actor);
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var route = await db.ProductionRoutes.Include(x => x.Stages).SingleAsync();
        route.Stages = route.Stages.ToList();
        var sewing = route.Stages.Single(x => x.StageId == config.SewingStageId);
        db.Remove(sewing);
        route.Stages.Remove(sewing);
        await db.SaveChangesAsync();
        route.Stages.Single(x => x.StageId == config.ReadyToPackStageId).Sequence = 2;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monday = today.AddDays(-21 - ((int)today.DayOfWeek + 6) % 7);
        var week = new ProductionScheduleWeek
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('B', 64),
            WeekStart = monday,
            WeekEnd = monday.AddDays(5),
            CreatedByUserId = actor.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        week.Lines.Add(new ProductionScheduleLine { Sequence = 1, PlannedDate = monday, ProductId = route.ProductId, Quantity = 5 });
        week.Lines.Add(new ProductionScheduleLine
        {
            Sequence = 2,
            PlannedDate = monday,
            ProductId = route.ProductId,
            Quantity = 3,
            IsCarryover = true,
            StartArea = ProductionDailyArea.Sewing
        });
        db.Add(week);
        await db.SaveChangesAsync();
        return new(week, actor.Id, route.ProductId, config);
    }

    private static async Task PublishAsync(WarehouseDbContext db, Setup setup)
    {
        var pins = new UserPinService(db, new PinProtector(PinKey));
        var schedule = new ProductionDailyScheduleService(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
        var result = await schedule.PublishAsync(new(Guid.NewGuid(), setup.Week.Id, setup.Week.Version, "4826", setup.Actor));
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        // Fixture represents an order published before universal daily stages were introduced.
        var line = await db.ProductionScheduleLines.SingleAsync(x => x.WeekId == setup.Week.Id && !x.IsCarryover);
        var order = await db.ProductionWorkOrders.Include(x => x.Stages).SingleAsync(x => x.Id == line.WorkOrderId);
        db.Remove(order.Stages.Single(x => x.SourceStageId == setup.Config.SewingStageId));
        order.Stages.Single(x => x.SourceStageId == setup.Config.ReadyToPackStageId).Sequence = 2;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static ProductionDailyCaptureService Captures(WarehouseDbContext db)
    {
        var pins = new UserPinService(db, new PinProtector(PinKey));
        return new(db, pins, new InventoryMovementService(db, pins, TimeProvider.System),
            new WarehouseClock(new WarehouseSettingsService(db)), TimeProvider.System);
    }

    private static async Task<Guid> CaptureAsync(WarehouseDbContext db, Setup setup, ProductionDailyArea area, decimal quantity, DateOnly? date = null)
    {
        db.ChangeTracker.Clear();
        var result = await Captures(db).ConfirmAsync(new(Guid.NewGuid(), date ?? setup.Week.WeekStart, area,
            setup.Config.Shift1Id!.Value, setup.Product, quantity, null, "4826"));
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        return result.Id!.Value;
    }

    private sealed record Setup(ProductionScheduleWeek Week, Guid Actor, Guid Product, ProductionDailyConfiguration Config);
}
