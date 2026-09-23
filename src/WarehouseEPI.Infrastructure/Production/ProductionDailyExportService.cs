using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public sealed class ProductionDailyExportService(
    WarehouseDbContext db,
    ProductionDailyBalanceService balances)
{
    public Task<byte[]?> ExportAsync(Guid weekId, CancellationToken token = default) => ExportAsync(weekId, null, token);

    public async Task<byte[]?> ExportAsync(Guid weekId, ProductionWeeklyFilter? weeklyFilter, CancellationToken token = default)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking().Include(x => x.Lines).ThenInclude(x => x.Product)
            .SingleOrDefaultAsync(x => x.Id == weekId, token);
        if (week is null) return null;
        var captures = await balances.GetCaptureDetailsAsync(weekId, token: token);
        var balance = await balances.GetAsync(weekId, token);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add($"{week.WeekStart:MM-dd} to {week.WeekEnd:MM-dd}");
        sheet.Cell(1, 1).Value = "PRODUCTION SCHEDULE";
        var planHeaders = new[] { "Day", "Part Number", "Qty", "Order 1", "Order 2", "Order 3", "Notes", "Origin" };
        WriteHeaders(sheet, 3, 1, planHeaders);
        var planRow = 4;
        foreach (var line in week.Lines.Where(x => !x.IsCancelled && !x.IsExtra).OrderBy(x => x.PlannedDate).ThenBy(x => x.Sequence).Take(10_000))
        {
            sheet.Cell(planRow, 1).Value = line.PlannedDate.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(planRow, 1).Style.DateFormat.Format = "yyyy-MM-dd";
            sheet.Cell(planRow, 2).Value = Safe(line.Product.Sku);
            sheet.Cell(planRow, 3).Value = line.Quantity;
            sheet.Cell(planRow, 4).Value = Safe(line.OrderReference1);
            sheet.Cell(planRow, 5).Value = Safe(line.OrderReference2);
            sheet.Cell(planRow, 6).Value = Safe(line.OrderReference3);
            sheet.Cell(planRow, 7).Value = Safe(line.Notes);
            sheet.Cell(planRow, 8).Value = line.IsCarryover ? "Carryover" : line.Origin.ToString();
            planRow++;
        }
        if (planRow > 4) sheet.Range(3, 1, planRow - 1, planHeaders.Length).CreateTable("WeeklyOrderPlanExport");

        var executionColumn = 10;
        sheet.Cell(1, executionColumn).Value = "DAILY EXECUTION";
        var executionHeaders = new[] { "Day", "Area", "Shift", "Part Number", "Pieces Completed", "Name", "Notes", "Status" };
        WriteHeaders(sheet, 3, executionColumn, executionHeaders);
        var executionRow = 4;
        foreach (var capture in captures.Take(10_000))
        {
            sheet.Cell(executionRow, executionColumn).Value = capture.EffectiveDate.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(executionRow, executionColumn).Style.DateFormat.Format = "yyyy-MM-dd";
            sheet.Cell(executionRow, executionColumn + 1).Value = Area(capture.Area);
            sheet.Cell(executionRow, executionColumn + 2).Value = Safe(capture.Shift);
            sheet.Cell(executionRow, executionColumn + 3).Value = Safe(capture.Sku);
            sheet.Cell(executionRow, executionColumn + 4).Value = capture.Quantity;
            sheet.Cell(executionRow, executionColumn + 5).Value = Safe(capture.Responsible);
            sheet.Cell(executionRow, executionColumn + 6).Value = Safe(capture.Notes);
            sheet.Cell(executionRow, executionColumn + 7).Value = capture.Status == ProductionDailyCaptureStatus.Active ? "Active" : "Reversed";
            executionRow++;
        }
        if (executionRow > 4) sheet.Range(3, executionColumn, executionRow - 1, executionColumn + executionHeaders.Length - 1)
            .CreateTable("AreaProductionEntryExport");

        var balanceRow = Math.Max(planRow, executionRow) + 3;
        sheet.Cell(balanceRow, 1).Value = "AUTOMATIC BALANCE";
        balanceRow += 2;
        var balanceHeaders = new[]
        {
            "Day", "Part Number", "New Plan", "Carryover", "Cutting Completed", "Cutting Pending",
            "Sewing Completed", "Sewing Pending", "Ready to Pack Completed", "Ready to Pack Pending", "Advance", "Progress %", "Cutting extra", "Sewing extra", "RTP extra", "Cutting to reconcile", "Sewing to reconcile", "RTP to reconcile"
        };
        WriteHeaders(sheet, balanceRow, 1, balanceHeaders);
        var balanceStart = balanceRow;
        balanceRow++;
        foreach (var row in balance?.Rows.Where(x => x.NewPlan != 0 || x.Carryover != 0 ||
                     x.Cutting.Completed != 0 || x.Sewing.Completed != 0 || x.ReadyToPack.Completed != 0 ||
                     x.Cutting.Pending != 0 || x.Sewing.Pending != 0 || x.ReadyToPack.Pending != 0).Take(10_000) ?? [])
        {
            sheet.Cell(balanceRow, 1).Value = row.Date.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(balanceRow, 1).Style.DateFormat.Format = "yyyy-MM-dd";
            sheet.Cell(balanceRow, 2).Value = Safe(row.Sku);
            sheet.Cell(balanceRow, 3).Value = row.NewPlan;
            sheet.Cell(balanceRow, 4).Value = row.Carryover;
            sheet.Cell(balanceRow, 5).Value = row.Cutting.Applies ? row.Cutting.Completed : "N/A";
            sheet.Cell(balanceRow, 6).Value = row.Cutting.Applies ? row.Cutting.Pending : "N/A";
            sheet.Cell(balanceRow, 7).Value = row.Sewing.Applies ? row.Sewing.Completed : "N/A";
            sheet.Cell(balanceRow, 8).Value = row.Sewing.Applies ? row.Sewing.Pending : "N/A";
            sheet.Cell(balanceRow, 9).Value = row.ReadyToPack.Applies ? row.ReadyToPack.Completed : "N/A";
            sheet.Cell(balanceRow, 10).Value = row.ReadyToPack.Applies ? row.ReadyToPack.Pending : "N/A";
            sheet.Cell(balanceRow, 11).Value = Math.Max(row.Cutting.Advance, Math.Max(row.Sewing.Advance, row.ReadyToPack.Advance));
            sheet.Cell(balanceRow, 12).Value = row.ProgressPercent / 100m;
            sheet.Cell(balanceRow, 12).Style.NumberFormat.Format = "0.0%";
            sheet.Cell(balanceRow, 13).Value = row.Cutting.Extra;
            sheet.Cell(balanceRow, 14).Value = row.Sewing.Extra;
            sheet.Cell(balanceRow, 15).Value = row.ReadyToPack.Extra;
            sheet.Cell(balanceRow, 16).Value = row.Cutting.ToReconcile;
            sheet.Cell(balanceRow, 17).Value = row.Sewing.ToReconcile;
            sheet.Cell(balanceRow, 18).Value = row.ReadyToPack.ToReconcile;
            balanceRow++;
        }
        if (balanceRow > balanceStart + 1)
            sheet.Range(balanceStart, 1, balanceRow - 1, balanceHeaders.Length).CreateTable("AutomaticBalanceExport");
        sheet.SheetView.FreezeRows(3);
        sheet.ColumnsUsed().AdjustToContents(8, 42);
        sheet.RangeUsed()!.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        var summary = await balances.GetWeeklyAsync(weekId, weeklyFilter ?? new(week.WeekEnd), token);
        if (summary is not null) WriteWeeklySummary(workbook, summary);
        var daily = await balances.GetDailySummaryAsync(weekId, weeklyFilter ?? new(week.WeekEnd), token);
        if (daily is not null) WriteDailySummary(workbook, daily);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteDailySummary(XLWorkbook workbook, ProductionDailySummary summary)
    {
        var sheet = workbook.Worksheets.Add("Balance diario");
        sheet.Cell(1, 1).Value = "DAILY PRODUCTION";
        sheet.Cell(2, 1).Value = summary.Through.ToDateTime(TimeOnly.MinValue);
        sheet.Cell(2, 1).Style.DateFormat.Format = "yyyy-MM-dd";
        sheet.Cell(2, 3).Value = summary.Status.ToString();
        var headers = new List<string> { "SKU", "Daily plan" };
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
            headers.AddRange(new[] { "Opening", "Completed today", "Closing pending", "Extra today", "To reconcile", "Scheduled carryover (intention)", "T1", "T2", "Pending for T2" }.Select(x => $"{Area(area)} {x}"));
        WriteHeaders(sheet, 4, 1, headers);
        var index = 5;
        foreach (var product in summary.Products)
        {
            sheet.Cell(index, 1).Value = Safe(product.Sku);
            sheet.Cell(index, 2).Value = product.Planned;
            var column = 3;
            foreach (var area in new[] { product.Cutting, product.Sewing, product.ReadyToPack })
            {
                foreach (var quantity in new[] { area.Opening, area.Completed, area.NetPending ?? area.Pending, area.Extra, area.ToReconcile })
                {
                    sheet.Cell(index, column).Value = area.Applies ? quantity : "N/A";
                    sheet.Cell(index, column++).Style.NumberFormat.Format = "0.####";
                }
                sheet.Cell(index, column++).Value = product.Intentions.Where(x => x.Area == area.Area).Sum(x => x.Quantity);
                sheet.Cell(index, column++).Value = area.Applies ? area.CompletedShift1 : "N/A";
                sheet.Cell(index, column++).Value = area.Applies ? area.CompletedShift2 : "N/A";
                sheet.Cell(index, column++).Value = area.Applies ? area.PendingAfterShift1 : "N/A";
            }
            index++;
        }
        if (index > 5) sheet.Range(4, 1, index - 1, headers.Count).CreateTable("DailyProductionSummary");
        sheet.SheetView.FreezeRows(4);
        sheet.SheetView.FreezeColumns(1);
        sheet.ColumnsUsed().AdjustToContents(10, 38);
    }

    private static void WriteWeeklySummary(XLWorkbook workbook, ProductionWeeklySummary summary)
    {
        var sheet = workbook.Worksheets.Add("Weekly summary");
        sheet.Cell(1, 1).Value = "WEEKLY PRODUCTION";
        sheet.Cell(2, 1).Value = $"{summary.WeekStart:yyyy-MM-dd} / {summary.WeekEnd:yyyy-MM-dd}";
        sheet.Cell(2, 3).Value = "Through";
        sheet.Cell(2, 4).Value = summary.Through.ToDateTime(TimeOnly.MinValue);
        sheet.Cell(2, 4).Style.DateFormat.Format = "yyyy-MM-dd";
        sheet.Cell(2, 6).Value = summary.Status.ToString();
        var headers = new List<string> { "SKU", "Description", "Weekly plan" };
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
            headers.AddRange(new[] { "Opening", "Completed", "Pending", "Extra", "To reconcile" }.Select(x => $"{Area(area)} {x}"));
        WriteHeaders(sheet, 4, 1, headers);
        var index = 5;
        foreach (var product in summary.Products)
        {
            sheet.Cell(index, 1).Value = Safe(product.Sku);
            sheet.Cell(index, 2).Value = Safe(product.Description);
            sheet.Cell(index, 3).Value = product.Planned;
            var column = 4;
            foreach (var area in new[] { product.Cutting, product.Sewing, product.ReadyToPack })
                foreach (var quantity in new[] { area.Opening, area.Completed, area.NetPending ?? area.Pending, area.Extra, area.ToReconcile })
                {
                    sheet.Cell(index, column).Value = area.Applies ? quantity : "N/A";
                    sheet.Cell(index, column++).Style.NumberFormat.Format = "0.####";
                }
            index++;
        }
        if (index > 5) sheet.Range(4, 1, index - 1, headers.Count).CreateTable("WeeklyProductionSummary");
        sheet.SheetView.FreezeRows(4);
        sheet.SheetView.FreezeColumns(1);
        sheet.ColumnsUsed().AdjustToContents(10, 38);
    }

    private static void WriteHeaders(IXLWorksheet sheet, int row, int column, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++) sheet.Cell(row, column + i).Value = headers[i];
        var range = sheet.Range(row, column, row, column + headers.Count - 1);
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#17324D");
        range.Style.Font.FontColor = XLColor.White;
    }
    private static string Area(ProductionDailyArea area) => area switch
    {
        ProductionDailyArea.Cutting => "Cutting",
        ProductionDailyArea.Sewing => "Sewing",
        _ => "Ready to Pack"
    };
    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Trim();
        return value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
    }
}
