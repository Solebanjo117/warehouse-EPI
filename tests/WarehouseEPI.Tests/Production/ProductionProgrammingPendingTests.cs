using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionProgrammingPendingTests
{
    [Fact]
    public async Task Empty_execution_respects_routes_and_dates_of_historical_openings()
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var product = await db.Products.SingleAsync();
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var route = await db.ProductionRoutes.Include(x => x.Stages).SingleAsync();
        route.Stages = route.Stages.ToList();
        var sewing = await db.ProductionRouteStages.SingleAsync(x => x.StageId == config.SewingStageId);
        db.Remove(sewing);
        var actor = new User { FullName = "Opening", RoleId = 1, PinLookup = "opening", PinHash = "test" };
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek { CreatedByUser = actor, RequestFingerprint = "opening", WeekStart = monday, WeekEnd = monday.AddDays(6) };
        week.Lines.Add(new() { Product = product, PlannedDate = monday, Quantity = 100, Sequence = 1 });
        week.Lines.Add(new()
        {
            Product = product,
            PlannedDate = monday.AddDays(1),
            Quantity = 7,
            Sequence = 2,
            IsCarryover = true,
            StartArea = ProductionDailyArea.ReadyToPack
        });
        db.Add(week); await db.SaveChangesAsync();
        var service = new ProductionDailyBalanceService(db);
        var first = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(monday)))!.Products);
        Assert.False(first.Sewing.Applies); Assert.Equal(100, first.ReadyToPack.NetPending);
        Assert.Equal(0, first.ReadyToPack.Opening);
        var second = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(monday.AddDays(1))))!.Products);
        Assert.Equal(107, second.ReadyToPack.Opening); Assert.Equal(107, second.ReadyToPack.NetPending);
        Assert.Equal(100, second.Cutting.NetPending);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Pending_counts_program_in_every_area_preserves_sign_and_does_not_change_history(bool explicitCarry, bool closed)
    {
        await using var db = ProductionOpeningImportTests.Context();
        await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        var product = await db.Products.SingleAsync(x => x.Sku == "FG-100");
        var config = await db.ProductionDailyConfigurations.SingleAsync();
        var actor = new User { FullName = "Pending test", RoleId = 1, PinLookup = Guid.NewGuid().ToString(), PinHash = "test" };
        var monday = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek
        {
            WeekStart = monday,
            WeekEnd = monday.AddDays(6),
            CreatedByUser = actor,
            RequestFingerprint = "pending",
            ExplicitCarryover = explicitCarry,
            Status = closed ? ProductionScheduleWeekStatus.Closed : ProductionScheduleWeekStatus.Open
        };
        week.Lines.Add(new() { Product = product, Sequence = 1, PlannedDate = monday, Quantity = 300 });
        week.Lines.Add(new() { Product = product, Sequence = 2, PlannedDate = monday.AddDays(1), Quantity = 100 });
        foreach (var (qty, shift, reversed) in new[] { (298m, config.Shift1Id!.Value, false), (22m, config.Shift2Id!.Value, false), (17m, config.Shift1Id.Value, true) })
            week.Captures.Add(new()
            {
                Product = product,
                EffectiveDate = monday,
                Area = ProductionDailyArea.Sewing,
                StageId = config.SewingStageId!.Value,
                ShiftId = shift,
                Quantity = qty,
                ResponsibleUser = actor,
                OperationId = Guid.NewGuid(),
                RequestFingerprint = "capture",
                Status = reversed ? ProductionDailyCaptureStatus.Reversed : ProductionDailyCaptureStatus.Active
            });
        db.Add(week); await db.SaveChangesAsync();
        var service = new ProductionDailyBalanceService(db);
        var physical = await service.GetPhysicalAsync(week.Id);
        var first = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(monday)))!.Products);
        Assert.Equal(2, first.Sewing.PendingAfterShift1);
        Assert.Equal(-20, first.Sewing.NetPending);
        Assert.Equal(300, first.Cutting.NetPending);
        Assert.Equal(300, first.ReadyToPack.NetPending);
        var next = Assert.Single((await service.GetDailySummaryAsync(week.Id, new(monday.AddDays(1))))!.Products);
        Assert.Equal(-20, next.Sewing.Opening);
        Assert.Equal(80, next.Sewing.NetPending);
        Assert.Equal(80, Assert.Single((await service.GetWeekCloseAsync(week.Id, new(week.WeekEnd)))!.Products).Sewing.Pending);
        using var exported = new XLWorkbook(new MemoryStream((await new ProductionDailyExportService(db, service).ExportAsync(week.Id))!));
        var table = exported.Worksheet(1).Table("AutomaticBalanceExport");
        Assert.Equal(-20, table.DataRange.Row(1).Cell(8).GetValue<decimal>());
        Assert.Equal(80, table.DataRange.Row(2).Cell(8).GetValue<decimal>());
        Assert.Equal(physical!.Rows.Select(x => x.Sewing), (await service.GetPhysicalAsync(week.Id))!.Rows.Select(x => x.Sewing));
        Assert.Equal(3, await db.ProductionDailyCaptures.CountAsync());
        Assert.Equal(closed ? ProductionScheduleWeekStatus.Closed : ProductionScheduleWeekStatus.Open, week.Status);
        Assert.All(db.ChangeTracker.Entries(), x => Assert.Equal(EntityState.Unchanged, x.State));
    }

    [Fact]
    public async Task Blank_type_monday_is_included_without_reading_excel_pending_formulas()
    {
        await using var db = ProductionOpeningImportTests.Context(); await db.Database.EnsureCreatedAsync();
        await ProductionDailyModuleTests.SeedImportCatalogAsync(db);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("9-28 TO 10-4");
        var headers = new[] { "Day", "Part Number", "Qty", "Tipo" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var days = new[] { "Monday", "Tuesday", "Wednesday" };
        for (var i = 0; i < days.Length; i++)
        {
            sheet.Cell(i + 2, 1).Value = days[i]; sheet.Cell(i + 2, 2).Value = "FG-100";
            sheet.Cell(i + 2, 3).Value = i == 0 ? 200 : 400;
        }
        sheet.Range(1, 1, 4, 4).CreateTable("WeeklyOrderPlan");
        sheet.Cell("Z169").Value = 800; // The source report's result is not the schedule.
        using var stream = new MemoryStream(); workbook.SaveAs(stream); stream.Position = 0;
        var preview = await new ProductionScheduleImportService(db, TimeProvider.System).PreviewAsync(stream, "source.xlsx",
            ProductionScheduleImportResolutions.None with { ReplaceProgramming = true });
        Assert.True(preview.CanConfirm, string.Join(";", preview.Issues.Select(x => x.Message)));
        var week = Assert.Single(preview.Weeks);
        Assert.Equal(1000, week.FinalLines.Sum(x => x.Quantity));
        Assert.Equal(200, Assert.Single(week.FinalLines, x => x.Date.DayOfWeek == DayOfWeek.Monday).Quantity);
    }
}
