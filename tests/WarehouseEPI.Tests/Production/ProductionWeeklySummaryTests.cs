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

public sealed class ProductionWeeklySummaryTests
{
    [Fact]
    public async Task Weekly_totals_and_export_include_more_than_500_captures_without_summing_daily_accumulators()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyAsync(db);
    }

    [Fact]
    public async Task Weekly_summary_and_grouped_lookup_translate_on_isolated_postgresql()
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
            await VerifyAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    internal static async Task VerifyAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var actor = new User { FullName = "Weekly report", RoleId = 1, PinHash = "", PinLookup = Guid.NewGuid().ToString("N") };
        var product = new Product { Sku = "ZZ-WEEK", Description = "Weekly product", BaseUnitId = 1 };
        var week = new ProductionScheduleWeek { WeekStart = new(2026, 7, 6), WeekEnd = new(2026, 7, 12),
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('W', 64), CreatedByUser = actor };
        week.Lines.Add(new() { Product = product, Sequence = 1, PlannedDate = week.WeekStart, Quantity = 550 });
        week.Lines.Add(new() { Product = product, Sequence = 2, PlannedDate = week.WeekEnd, Quantity = 50,
            OrderReference2 = "ORDER-2", OriginalType = "Programación nueva",
            OriginalAnnotation1 = "YA CORTADO", OriginalAnnotation1Kind = "Text",
            OriginalAnnotation2 = "2026-07-05", OriginalAnnotation2Kind = "Date" });
        week.Lines.Add(new() { Product = product, Sequence = 3, PlannedDate = week.WeekStart,
            Quantity = 3, IsCarryover = true, StartArea = ProductionDailyArea.Sewing });
        db.Add(week);
        await db.SaveChangesAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        for (var i = 0; i < 503; i++)
            db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = product.Id, Area = ProductionDailyArea.Cutting,
                StageId = config.CuttingStageId!.Value, ShiftId = i % 2 == 0 ? config.Shift1Id!.Value : config.Shift2Id!.Value,
                Quantity = 1, EffectiveDate = i < 501 ? week.WeekStart : week.WeekEnd, ResponsibleUserId = actor.Id,
                OperationId = Guid.NewGuid(), RequestFingerprint = new string('C', 64), Origin = ProductionScheduleOrigin.ExcelImport });
        db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = product.Id, Area = ProductionDailyArea.Cutting,
            StageId = config.CuttingStageId!.Value, ShiftId = config.Shift1Id!.Value, Quantity = 999,
            Status = ProductionDailyCaptureStatus.Reversed, EffectiveDate = week.WeekStart, ResponsibleUserId = actor.Id,
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('R', 64), ReversedByUserId = actor.Id,
            ReversedAt = DateTimeOffset.UtcNow, ReverseReason = "Test" });
        db.ProductionCarryoverPlans.Add(new() { WeekId = week.Id, ProductId = product.Id, PlannedDate = week.WeekStart,
            Area = ProductionDailyArea.Sewing, Quantity = 2, UpdatedByUserId = actor.Id });
        await db.SaveChangesAsync();
        var service = new ProductionDailyBalanceService(db);
        var filter = new ProductionWeeklyFilter(week.WeekStart.AddDays(2), "zz-week");
        var summary = (await service.GetWeeklyAsync(week.Id, filter))!;
        var row = Assert.Single(summary.Products);
        Assert.Equal(600, row.Planned);
        Assert.Equal(501, row.Cutting.Completed);
        Assert.Equal(49, row.Cutting.Pending);
        Assert.Equal(3, row.Sewing.Opening);
        Assert.Equal(504, row.Sewing.Pending);
        Assert.Equal(501, row.Days.Sum(x => x.Shifts.Sum(s => s.Quantity)));
        Assert.Equal(251, row.Days[0].Shifts.Single(x => x.ShiftId == config.Shift1Id).Quantity);
        Assert.Equal(2, Assert.Single(row.Intentions).Quantity);
        Assert.Equal(50, row.Days[^1].Planned);
        Assert.Equal(503, Assert.Single((await service.GetWeeklyAsync(week.Id, new(week.WeekEnd)))!.Products).Cutting.Completed);
        Assert.Empty((await service.GetWeeklyAsync(week.Id, filter with { Sku = "missing" }))!.Products);
        var close = (await service.GetWeekCloseAsync(week.Id, filter))!;
        var closingProduct = Assert.Single(close.Products);
        Assert.Equal(week.WeekEnd, close.WeekEnd);
        Assert.Equal(50m, closingProduct.SundayPlan);
        Assert.Equal(600m, closingProduct.WeeklyPlan);
        Assert.Equal(503m, closingProduct.Cutting.Completed);
        Assert.Equal(97m, closingProduct.Cutting.Pending);
        Assert.Equal(503m / 600m, closingProduct.Cutting.CompletionRatio(closingProduct.WeeklyPlan));
        Assert.Equal(600m, Assert.Single(close.Totals).WeeklyPlan);
        Assert.Equal(600m, close.SummaryTotal.WeeklyPlan);
        Assert.Equal(503m, close.SummaryTotal.Cutting);
        var initialComparison = Assert.Single(close.ShiftComparison);
        var cuttingShifts = initialComparison.Areas.Single(x => x.Area == ProductionDailyArea.Cutting);
        Assert.Equal(252m, cuttingShifts.Shift1);
        Assert.Equal(251m, cuttingShifts.Shift2);
        Assert.Equal(503m, initialComparison.Total);
        Assert.Equal(252m / 503m, cuttingShifts.Shift1Share);
        Assert.Null(initialComparison.Areas.Single(x => x.Area == ProductionDailyArea.Sewing).Shift1Share);
        Assert.Empty((await service.GetWeekCloseAsync(week.Id, filter with { Sku = "missing" }))!.ShiftComparison);
        Assert.Single(Assert.Single((await service.GetWeekCloseAsync(week.Id,
            filter with { Area = ProductionDailyArea.Cutting }))!.ShiftComparison).Areas);
        Assert.Empty((await service.GetWeekCloseAsync(week.Id, filter with { Sku = "missing" }))!.Products);
        using var workbook = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, service).ExportAsync(week.Id, filter))!));
        var sheet = workbook.Worksheet("Weekly summary");
        Assert.Equal("ZZ-WEEK", sheet.Cell(5, 1).GetString());
        Assert.Equal(600m, sheet.Cell(5, 3).GetValue<decimal>());
        Assert.Equal(row.Cutting.Completed, sheet.Cell(5, 5).GetValue<decimal>());
        Assert.Equal(row.Cutting.Pending, sheet.Cell(5, 6).GetValue<decimal>());
        Assert.Equal(3m, sheet.Cell(5, 9).GetValue<decimal>());
        Assert.Equal(summary.Through.ToDateTime(TimeOnly.MinValue), sheet.Cell(2, 4).GetDateTime());
        var pendingSheet = workbook.Worksheet("Pendiente proxima semana");
        Assert.Equal(week.WeekEnd.ToDateTime(TimeOnly.MinValue), pendingSheet.Cell(2, 1).GetDateTime());
        Assert.Equal(50m, pendingSheet.Cell(5, 3).GetValue<decimal>());
        Assert.Equal(97m, pendingSheet.Cell(5, 8).GetValue<decimal>());
        var completionSheet = workbook.Worksheet("Cumplimiento semanal");
        var shiftSheet = workbook.Worksheet("Comparacion de turnos");
        Assert.Equal("Corte", shiftSheet.Cell(6, 2).GetString());
        Assert.Equal(252m, shiftSheet.Cell(6, 3).GetValue<decimal>());
        Assert.Equal(251m, shiftSheet.Cell(6, 4).GetValue<decimal>());
        Assert.InRange(shiftSheet.Cell(6, 6).GetValue<decimal>(), 0.50099m, 0.50101m);
        Assert.Equal("—", shiftSheet.Cell(7, 6).GetString());
        var partSummarySheet = workbook.Worksheet("Resumen produccion semanal");
        Assert.Equal(600m, partSummarySheet.Cell(5, 3).GetValue<decimal>());
        Assert.Equal(503m, partSummarySheet.Cell(5, 4).GetValue<decimal>());
        Assert.Equal("Ready to Pack completado", partSummarySheet.Cell(4, 6).GetString());
        Assert.True(partSummarySheet.Cell(4, 7).IsEmpty());
        Assert.Equal(600m, completionSheet.Cell(5, 3).GetValue<decimal>());
        Assert.Equal(503m, completionSheet.Cell(5, 4).GetValue<decimal>());
        Assert.InRange(completionSheet.Cell(5, 5).GetValue<decimal>(), 0.838333333333m, 0.838333333334m);
        var planSheet = workbook.Worksheet("Resumen del programa");
        Assert.Equal(50m, planSheet.Cell(10, 4).GetValue<decimal>());
        Assert.Equal(1, planSheet.Cell(10, 7).GetValue<int>());
        Assert.Equal(550m, planSheet.Cell(4, 5).GetValue<decimal>());
        var detailSheet = workbook.Worksheet(1);
        var sundayPlanRow = Enumerable.Range(4, 3).Single(r => detailSheet.Cell(r, 1).GetDateTime().Date == week.WeekEnd.ToDateTime(TimeOnly.MinValue).Date);
        Assert.Equal("Programación nueva", detailSheet.Cell(sundayPlanRow, 10).GetString());
        Assert.Equal("YA CORTADO", detailSheet.Cell(sundayPlanRow, 11).GetString());
        Assert.Equal("Date", detailSheet.Cell(sundayPlanRow, 14).GetString());
        var monday = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart)))!.Products);
        Assert.Equal(550, monday.Planned);
        Assert.Equal(501, monday.Cutting.Completed);
        Assert.Equal(251, monday.Cutting.CompletedShift1);
        Assert.Equal(250, monday.Cutting.CompletedShift2);
        Assert.Equal(299, monday.Cutting.PendingAfterShift1);
        Assert.Equal(254, monday.Sewing.PendingAfterShift1);
        Assert.Equal(3, monday.Sewing.Opening);
        Assert.Equal(504, monday.Sewing.Pending);
        var tuesday = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart.AddDays(1))))!.Products);
        Assert.Equal(0, tuesday.Planned);
        Assert.Equal(0, tuesday.Cutting.Completed);
        Assert.Equal(0, tuesday.Cutting.CompletedShift1);
        Assert.Equal(0, tuesday.Cutting.CompletedShift2);
        Assert.Equal(49, tuesday.Cutting.Opening);
        Assert.Equal(504, tuesday.Sewing.Opening);
        Assert.Empty(tuesday.Intentions);
        var saturday = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekEnd)))!.Products);
        Assert.Equal(50, saturday.Planned);
        Assert.Equal(2, saturday.Cutting.Completed);
        Assert.Equal(1, saturday.Cutting.CompletedShift1);
        Assert.Equal(1, saturday.Cutting.CompletedShift2);
        Assert.Equal(49, saturday.Cutting.Opening);
        Assert.Equal(97, saturday.Cutting.Pending);
        var dailySheet = workbook.Worksheet("Balance diario");
        using var mondayBook = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, service).ExportAsync(week.Id, new ProductionWeeklyFilter(week.WeekStart)))!));
        Assert.Equal(251m, mondayBook.Worksheet("Balance diario").Cell(5, 9).GetValue<decimal>());
        Assert.Equal(250m, mondayBook.Worksheet("Balance diario").Cell(5, 10).GetValue<decimal>());
        Assert.Equal(299m, mondayBook.Worksheet("Balance diario").Cell(5, 11).GetValue<decimal>());
        Assert.Equal(0m, dailySheet.Cell(5, 2).GetValue<decimal>());
        Assert.Equal(49m, dailySheet.Cell(5, 3).GetValue<decimal>());
        Assert.Equal(0m, dailySheet.Cell(5, 4).GetValue<decimal>());
        Assert.Equal(49m, dailySheet.Cell(5, 5).GetValue<decimal>());
        Assert.Equal(summary.Through.ToDateTime(TimeOnly.MinValue), dailySheet.Cell(2, 1).GetDateTime());
        db.ProductionScheduleLines.Add(new() { WeekId = week.Id, ProductId = product.Id, Sequence = 4,
            PlannedDate = week.WeekStart.AddDays(2), Quantity = 7, IsCarryover = true,
            StartArea = ProductionDailyArea.Sewing, Origin = ProductionScheduleOrigin.ExcelImport });
        await db.SaveChangesAsync();
        Assert.Equal(3, Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart)))!.Products).Sewing.Opening);
        var wednesday = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart.AddDays(2))))!.Products);
        Assert.Equal(511, wednesday.Sewing.Opening);
        Assert.Equal(511, wednesday.Sewing.Pending);
        Assert.Equal(511, Assert.Single((await service.GetDailySummaryAsync(week.Id, new(week.WeekStart.AddDays(3))))!.Products).Sewing.Opening);
        await VerifyLookupAsync(db, week, product, actor);

        var box = new Product { Sku = "BX-CLOSE", BaseUnitId = 2 };
        var noPlan = new Product { Sku = "NO-PLAN-CLOSE", BaseUnitId = 1 };
        db.AddRange(box, noPlan);
        db.ProductionScheduleLines.Add(new() { WeekId = week.Id, ProductId = box.Id,
            Sequence = 50, PlannedDate = week.WeekEnd, Quantity = 10 });
        foreach (var (item, quantity) in new[] { (box, 12m), (noPlan, 4m) })
            db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = item.Id, Area = ProductionDailyArea.Cutting,
                StageId = config.CuttingStageId!.Value, ShiftId = config.Shift1Id!.Value,
                Quantity = quantity, EffectiveDate = week.WeekEnd, ResponsibleUserId = actor.Id,
                OperationId = Guid.NewGuid(), RequestFingerprint = new string('P', 64),
                Origin = ProductionScheduleOrigin.ExcelImport });
        await db.SaveChangesAsync();
        var mixedClose = (await service.GetWeekCloseAsync(week.Id, new(week.WeekStart)))!;
        Assert.Equal(2, mixedClose.Totals.Count);
        Assert.Equal(1.2m, mixedClose.Products.Single(x => x.ProductId == box.Id)
            .Cutting.CompletionRatio(10));
        Assert.Null(mixedClose.Products.Single(x => x.ProductId == noPlan.Id)
            .Cutting.CompletionRatio(0));
        Assert.Equal(10m, mixedClose.Totals.Single(x => x.Unit == "BX").WeeklyPlan);
        Assert.Equal(12m, mixedClose.Totals.Single(x => x.Unit == "BX").Cutting.Completed);
        Assert.Equal(632m, mixedClose.SummaryTotal.WeeklyPlan);
        Assert.Equal(519m, mixedClose.SummaryTotal.Cutting);
        Assert.Equal(2, mixedClose.ShiftComparison.Count);
        Assert.Equal(12m, mixedClose.ShiftComparison.Single(x => x.Unit == "BX").Total);
        Assert.Equal(0m, mixedClose.ShiftComparison.Single(x => x.Unit == "BX").Shift2);
        using var mixedWorkbook = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, service)
            .ExportAsync(week.Id, new ProductionWeeklyFilter(week.WeekStart)))!));
        var mixedSummary = mixedWorkbook.Worksheet("Resumen produccion semanal");
        var totalRow = Assert.Single(mixedSummary.RowsUsed(), x => x.Cell(1).GetString() == "Total filtrado");
        Assert.True(totalRow.Cell(2).IsEmpty());
        Assert.Equal(632m, totalRow.Cell(3).GetValue<decimal>());
        Assert.Equal(519m, totalRow.Cell(4).GetValue<decimal>());
        db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = product.Id,
            Area = ProductionDailyArea.ReadyToPack, StageId = config.ReadyToPackStageId!.Value,
            ShiftId = config.Shift1Id!.Value, Quantity = 30, EffectiveDate = week.WeekStart,
            ResponsibleUserId = actor.Id, OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('T', 64), Origin = ProductionScheduleOrigin.ExcelImport });
        db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = product.Id,
            Area = ProductionDailyArea.ReadyToPack, StageId = config.ReadyToPackStageId!.Value,
            ShiftId = config.Shift2Id!.Value, Quantity = 10, EffectiveDate = week.WeekStart,
            ResponsibleUserId = actor.Id, OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('U', 64), Origin = ProductionScheduleOrigin.ExcelImport });
        db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = product.Id,
            Area = ProductionDailyArea.ReadyToPack, StageId = config.ReadyToPackStageId!.Value,
            ShiftId = config.Shift1Id!.Value, Quantity = 999, EffectiveDate = week.WeekStart,
            Status = ProductionDailyCaptureStatus.Reversed, ResponsibleUserId = actor.Id,
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('V', 64),
            ReversedByUserId = actor.Id, ReversedAt = DateTimeOffset.UtcNow, ReverseReason = "Test" });
        await db.SaveChangesAsync();
        var withReadyToPack = (await service.GetWeekCloseAsync(week.Id, new(week.WeekStart)))!;
        var ready = withReadyToPack.ShiftComparison.Single(x => x.Unit == "EA").Areas
            .Single(x => x.Area == ProductionDailyArea.ReadyToPack);
        Assert.Equal(30m, ready.Shift1);
        Assert.Equal(10m, ready.Shift2);
        Assert.Equal(0.75m, ready.Shift1Share);
        var status = (await service.GetDailySummaryAsync(week.Id, new(week.WeekStart)))!.Products
            .Single(x => x.ProductId == product.Id);
        Assert.Equal(40m / 550m, status.StatusRatio);
        using var withReadyExport = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, service)
             .ExportAsync(week.Id, new ProductionWeeklyFilter(week.WeekStart)))!));
        Assert.Equal("Status %", withReadyExport.Worksheet("Balance diario").Cell(4, 30).GetString());
        var exportedStatusRow = Assert.Single(withReadyExport.Worksheet("Balance diario").RowsUsed(),
            x => x.Cell(1).GetString() == "ZZ-WEEK");
        Assert.InRange(exportedStatusRow.Cell(30).GetValue<decimal>(), 0.0727m, 0.0728m);
        var exportedReadyRow = Assert.Single(withReadyExport.Worksheet("Comparacion de turnos").RowsUsed(),
            x => x.Cell(1).GetString() == "EA" && x.Cell(2).GetString() == "Ready to Pack");
        Assert.Equal(40m, exportedReadyRow.Cell(5).GetValue<decimal>());
    }

    private static async Task VerifyLookupAsync(WarehouseDbContext db, ProductionScheduleWeek week, Product planned, User actor)
    {
        var products = Enumerable.Range(0, 24).Select(i => new Product { Sku = $"AA-WEEK-{i:00}", BaseUnitId = 1 }).ToArray();
        db.AddRange(products);
        var inactive = new Product { Sku = "AA-WEEK-INACTIVE", BaseUnitId = 1, IsActive = false };
        db.Add(inactive);
        // Duplicate plan lines must not duplicate suggestions, and a planned product sorts before catalog matches.
        foreach (var (product, i) in products.Take(12).Select((p, i) => (p, i)))
            db.ProductionScheduleLines.Add(new() { WeekId = week.Id, ProductId = product.Id, Sequence = 10 + i,
                PlannedDate = week.WeekStart, Quantity = 1 });
        db.ProductionScheduleLines.Add(new() { WeekId = week.Id, ProductId = products[14].Id, Sequence = 29,
            PlannedDate = week.WeekEnd, Quantity = 10 });
        db.ProductionCarryoverPlans.Add(new() { WeekId = week.Id, ProductId = products[13].Id,
            PlannedDate = week.WeekStart, Area = ProductionDailyArea.Cutting, Quantity = 2, UpdatedByUserId = actor.Id });
        await db.SaveChangesAsync();
        var futureProduct = (await new ProductionDailyBalanceService(db).GetDailySummaryAsync(week.Id, new(week.WeekStart)))!
            .Products.Single(x => x.ProductId == products[14].Id);
        Assert.Equal(0, futureProduct.Planned);
        Assert.Equal(0, futureProduct.Cutting.Pending);
        Assert.Equal(10, (await new ProductionDailyBalanceService(db).GetDailySummaryAsync(week.Id, new(week.WeekEnd)))!
            .Products.Single(x => x.ProductId == products[14].Id).Planned);
        var pins = new UserPinService(db, new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="));
        var capture = new ProductionDailyCaptureService(db, pins, new InventoryMovementService(db, pins, TimeProvider.System),
            new WarehouseClock(new WarehouseSettingsService(db)), TimeProvider.System);
        var groups = await capture.SearchDailyProductsAsync(week.WeekStart, ProductionDailyArea.Cutting, "week");
        Assert.True(groups[0].HasMore);
        Assert.Equal(10, groups[0].Items.Count);
        var next = Assert.Single(await capture.SearchDailyProductsAsync(week.WeekStart, ProductionDailyArea.Cutting, "week", 0, 10));
        Assert.Contains(next.Items, x => x.Id == planned.Id);
        Assert.False(next.HasMore);
        Assert.DoesNotContain(groups.SelectMany(x => x.Items), x => x.Id == inactive.Id);
        var initial = await capture.SearchDailyProductsAsync(week.WeekStart, ProductionDailyArea.Cutting, null);
        Assert.DoesNotContain(initial, x => x.Group == 2);
        var exact = await capture.SearchDailyProductsAsync(week.WeekStart, ProductionDailyArea.Cutting, "aa-week-01");
        Assert.Equal(products[1].Id, exact[0].Items[0].Id);
        var all = groups[0].Items.Concat(next.Items).ToArray();
        Assert.Equal(all.Length, all.Select(x => x.Id).Distinct().Count());
        Assert.Contains(all, x => x.Id == products[13].Id);
    }
}
