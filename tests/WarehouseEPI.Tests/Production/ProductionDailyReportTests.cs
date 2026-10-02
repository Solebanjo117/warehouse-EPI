using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionDailyReportTests
{
    [Fact]
    public async Task Reports_preserve_effective_quantities_units_filters_and_balance_semantics()
    {
        await using var db = ProductionOpeningImportTests.Context();
        await VerifyAsync(db);
    }

    [Fact]
    public async Task Reports_translate_on_isolated_postgresql()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var database = "warehouse_epi_report_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_report_test_[a-f0-9]{32}$", database);
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var testBuilder = new NpgsqlConnectionStringBuilder(source) { Database = database, Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testBuilder.ConnectionString).Options);
            await VerifyAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var actor = new User { FullName = "Report test", RoleId = 1, PinHash = "test", PinLookup = Guid.NewGuid().ToString() };
        var product = new Product { Sku = "REPORT-A", Description = "Needle work", BaseUnitId = 1 };
        var second = new Product { Sku = "REPORT-B", Description = "Second unit", BaseUnit = new Unit { Code = "TEST-M", Name = "Test metres" } };
        var week = new ProductionScheduleWeek { WeekStart = new(2026, 9, 21), WeekEnd = new(2026, 9, 27), ExplicitCarryover = true, Status = ProductionScheduleWeekStatus.Closed,
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('W', 64), CreatedByUser = actor };
        week.Lines.Add(new() { Product = product, Sequence = 1, PlannedDate = week.WeekStart, Quantity = 500, OrderReference1 = "ORDER-A" });
        week.Lines.Add(new() { Product = second, Sequence = 2, PlannedDate = week.WeekStart, Quantity = 10, OrderReference1 = "ORDER-B" });
        var oldShift = new ProductionShift { Name = "Historical shift", Code = "OLD" };
        db.AddRange(week, oldShift);
        await db.SaveChangesAsync();
        var configuration = await db.ProductionDailyConfigurations.SingleAsync();
        db.ProductionRoutes.Add(new() { ProductId = second.Id, Name = "Ready only", Stages =
            [new ProductionRouteStage { StageId = configuration.ReadyToPackStageId!.Value, Sequence = 1 }] });
        var prior = new ProductionScheduleWeek { WeekStart = week.WeekStart.AddDays(-7), WeekEnd = week.WeekStart.AddDays(-1),
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('P', 64), CreatedByUserId = actor.Id };
        var priorLine = new ProductionScheduleLine { WeekId = prior.Id, ProductId = product.Id, Sequence = 1, PlannedDate = prior.WeekStart, Quantity = 7 };
        prior.Lines.Add(priorLine);
        db.Add(prior);
        db.ProductionWeekOpenings.Add(new() { WeekId = week.Id, SourceWeekId = prior.Id, SourceLineId = priorLine.Id,
            ProductId = product.Id, Area = ProductionDailyArea.Sewing, Quantity = 7 });
        for (var i = 0; i < 503; i++)
            db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = product.Id, Area = ProductionDailyArea.Cutting,
                StageId = configuration.CuttingStageId!.Value, ShiftId = i % 2 == 0 ? configuration.Shift1Id!.Value : configuration.Shift2Id!.Value,
                EffectiveDate = i < 501 ? week.WeekStart : week.WeekEnd, Quantity = 1, ResponsibleUserId = actor.Id,
                OperationId = Guid.NewGuid(), RequestFingerprint = new string('C', 64), Origin = ProductionScheduleOrigin.ExcelImport });
        db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = product.Id, Area = ProductionDailyArea.Cutting,
            StageId = configuration.CuttingStageId!.Value, ShiftId = oldShift.Id, EffectiveDate = week.WeekStart,
            Quantity = 2.125m, ResponsibleUserId = actor.Id, OperationId = Guid.NewGuid(), RequestFingerprint = new string('H', 64), Origin = ProductionScheduleOrigin.ExcelImport });
        db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = product.Id, Area = ProductionDailyArea.Cutting,
            StageId = configuration.CuttingStageId!.Value, ShiftId = oldShift.Id, EffectiveDate = week.WeekStart,
            Quantity = 999, Status = ProductionDailyCaptureStatus.Reversed, ReversedAt = DateTimeOffset.UtcNow,
            ReversedByUserId = actor.Id, ReverseReason = "Corrected", ResponsibleUserId = actor.Id,
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('R', 64) });
        db.ProductionDailyCaptures.Add(new() { WeekId = week.Id, ProductId = product.Id, Area = ProductionDailyArea.Sewing,
            StageId = configuration.SewingStageId!.Value, ShiftId = configuration.Shift2Id!.Value, EffectiveDate = week.WeekStart,
            Quantity = 3, ResponsibleUserId = actor.Id, OperationId = Guid.NewGuid(), RequestFingerprint = new string('S', 64) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var balances = new ProductionDailyBalanceService(db);
        var service = new ProductionReportService(db, balances);
        var filter = new ProductionReportFilter(week.Id, week.WeekStart);
        var summary = (await service.GetAsync(filter))!;
        Assert.Equal(7, summary.Rows.Count);
        Assert.Single(summary.Totals);
        Assert.Equal(7, summary.Rows.Select(x => x.Date).Distinct().Count());
        Assert.Equal(ProductionScheduleWeekStatus.Closed, summary.Status);
        Assert.DoesNotContain(ProductionReportTable.Create(summary).Columns, x => x.Label == "Unidad");
        Assert.Equal(505.125m, summary.Totals.Sum(x => x.Areas.Single(a => a.Area == ProductionDailyArea.Cutting).Total));
        Assert.Equal(503.125m, summary.Rows.Where(x => x.Date == week.WeekStart).Sum(x => x.Areas[0].Total));
        Assert.Equal(2m, summary.Rows.Where(x => x.Date == week.WeekEnd).Sum(x => x.Areas[0].Total));
        Assert.Contains(ProductionDailyArea.Cutting, summary.OtherShiftAreas);
        var detailed = (await service.GetAsync(filter with { Products = true, Sku = "nEEdle", Reference = "order-a" }))!;
        var row = Assert.Single(detailed.Rows);
        var day = Assert.Single((await balances.GetDailySummaryAsync(week.Id, new(week.WeekStart, "REPORT-A")))!.Products);
        Assert.Equal(day.Cutting.PendingAfterShift1, row.Areas[0].PendingAfterShift1);
        Assert.Equal(day.Cutting.NetPending, row.Areas[0].Pending);
        Assert.Equal(-3.125m, row.Areas[0].Pending);
        Assert.Equal(2.125m, row.Areas[0].Others);
        Assert.Equal(7m, row.Areas[1].Opening);
        Assert.Equal(7m, row.Areas[1].PlannedCarryover);
        Assert.Equal(day.Sewing.SignedPending, row.Areas[1].Pending);
        var full = (await service.GetAsync(filter with { Products = true, FullWeek = true, Sku = "REPORT-A" }))!;
        var weekly = Assert.Single((await balances.GetWeeklyAsync(week.Id, new(week.WeekEnd, "REPORT-A")))!.Products);
        Assert.Equal(weekly.Cutting.Opening, full.Rows[0].Areas[0].Opening);
        Assert.Equal(weekly.Cutting.NetPending, full.Rows[0].Areas[0].Pending);
        Assert.Equal(505.125m, full.Rows[0].Areas[0].Total);
        Assert.DoesNotContain(ProductionReportTable.Create(full).Columns, x => x.Label == "Pendiente para T2");
        var filtered = (await service.GetAsync(filter with { Area = ProductionDailyArea.ReadyToPack, Reference = "ORDER-B" }))!;
        Assert.Equal(7, filtered.Rows.Count);
        Assert.Single(filtered.Areas);
        Assert.All(filtered.Rows, x => Assert.Single(x.Areas));
        Assert.Empty((await service.GetAsync(filter with { Sku = "MISSING" }))!.Rows);
        var outside = (await service.GetAsync(filter with { Products = true, Date = week.WeekEnd.AddDays(1) }))!;
        Assert.Equal(week.WeekEnd, outside.Filter.Date);
        Assert.Empty(db.ChangeTracker.Entries());

        var table = ProductionReportTable.Create(detailed);
        Assert.DoesNotContain(table.Columns, x => x.Label == "Por conciliar");
        var pendingColumn = table.Columns.Select((column, index) => (column, index))
            .Single(x => x.column.Group == "Corte" && x.column.Label == "Pendiente final").index;
        Assert.Equal(-3.125m, table.Rows[0].Cells[pendingColumn]);
        using var book = new XLWorkbook(new MemoryStream(ProductionReportExportService.Export(detailed, DateTimeOffset.UtcNow, x => x)));
        var sheet = book.Worksheet(1);
        for (var column = 0; column < table.Rows[0].Cells.Count; column++)
            if (table.Rows[0].Cells[column] is decimal quantity) Assert.Equal(quantity, sheet.Cell(9, column + 1).GetValue<decimal>());
        Assert.Contains(sheet.MergedRanges, x => x.RangeAddress.FirstAddress.RowNumber == 7);
        Assert.Equal(XLPageOrientation.Landscape, sheet.PageSetup.PageOrientation);
        var processStarts = table.Columns.Select((column, index) => (column.Group, Index: index + 1))
            .Where(x => x.Group is not null).GroupBy(x => x.Group).Select(x => x.First().Index).ToArray();
        Assert.Equal(3, processStarts.Select(x => sheet.Cell(7, x).Style.Fill.BackgroundColor).Distinct().Count());
        foreach (var column in processStarts)
        {
            Assert.Equal(XLColor.White, sheet.Cell(8, column).Style.Font.FontColor);
            Assert.NotEqual(sheet.Cell(7, column).Style.Fill.BackgroundColor, sheet.Cell(9, column).Style.Fill.BackgroundColor);
            Assert.NotEqual(sheet.Cell(9, column).Style.Fill.BackgroundColor,
                sheet.Cell(9 + table.Rows.Count, column).Style.Fill.BackgroundColor);
        }
        Assert.Equal(506, await db.ProductionDailyCaptures.CountAsync());
    }

    [Theory]
    [InlineData(10_000, true)]
    [InlineData(10_001, false)]
    public void Output_limit_is_explicit_without_silent_truncation(int count, bool allowed)
    {
        var row = new ProductionReportRow(null, "=HYPERLINK(test)", "+formula", "PCS", null, -1.125m, []);
        var report = new ProductionReport(new(Guid.NewGuid(), new(2026, 9, 21), true, true), new(2026, 9, 21), new(2026, 9, 27),
            ProductionScheduleWeekStatus.Closed, Enumerable.Repeat(row, count).ToArray(), [], [], new HashSet<ProductionDailyArea>(), "First", "Second");
        Assert.Equal(allowed, report.CanOutput);
        if (!allowed) Assert.Throws<InvalidOperationException>(() => ProductionReportExportService.Export(report, DateTimeOffset.UtcNow, x => x));
        else
        {
            using var book = new XLWorkbook(new MemoryStream(ProductionReportExportService.Export(report, DateTimeOffset.UtcNow, x => x)));
            Assert.Equal(count + 8, book.Worksheet(1).LastRowUsed()!.RowNumber());
            Assert.False(book.Worksheet(1).Cell(9, 1).HasFormula);
            Assert.True(book.Worksheet(1).Cell(9, 1).Style.IncludeQuotePrefix);
            Assert.Equal(-1.125m, book.Worksheet(1).Cell(9, 2).GetValue<decimal>());
        }
    }
}
