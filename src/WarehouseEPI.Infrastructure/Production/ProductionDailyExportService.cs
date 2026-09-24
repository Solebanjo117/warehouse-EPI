using ClosedXML.Excel;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace WarehouseEPI.Infrastructure.Production;

public sealed class ProductionDailyExportService(
    WarehouseDbContext db,
    ProductionDailyBalanceService balances)
{
    public Task<byte[]?> ExportAsync(Guid weekId, CancellationToken token = default) => ExportAsync(weekId, null, token);

    public async Task<byte[]?> ExportAsync(Guid weekId, ProductionWeeklyFilter? weeklyFilter, CancellationToken token = default)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking().Include(x => x.Lines).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
            .SingleOrDefaultAsync(x => x.Id == weekId, token);
        if (week is null) return null;
        var captures = await balances.GetCaptureDetailsAsync(weekId, token: token);
        var balance = await balances.GetAsync(weekId, token);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add($"{week.WeekStart:MM-dd} to {week.WeekEnd:MM-dd}");
        sheet.Cell(1, 1).Value = "PRODUCTION SCHEDULE";
        var planHeaders = new[] { "Day", "Part Number", "Qty", "Order 1", "Order 2", "Order 3", "Notes", "Origin", "Unit", "Original Type", "Column1", "Column1 Kind", "Column2", "Column2 Kind" };
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
            sheet.Cell(planRow, 9).Value = Safe(line.Product.BaseUnit.Code);
            sheet.Cell(planRow, 10).Value = Safe(line.OriginalType);
            sheet.Cell(planRow, 11).Value = Safe(line.OriginalAnnotation1);
            sheet.Cell(planRow, 12).Value = Safe(line.OriginalAnnotation1Kind);
            sheet.Cell(planRow, 13).Value = Safe(line.OriginalAnnotation2);
            sheet.Cell(planRow, 14).Value = Safe(line.OriginalAnnotation2Kind);
            planRow++;
        }
        if (planRow > 4) sheet.Range(3, 1, planRow - 1, planHeaders.Length).CreateTable("WeeklyOrderPlanExport");

        var executionColumn = 16;
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
        var close = await balances.GetWeekCloseAsync(weekId, weeklyFilter ?? new(week.WeekEnd), token);
        if (close is not null) WriteWeekClose(workbook, close);
        await WritePlanSummaryAsync(workbook, week, token);
        if (week.ExplicitCarryover)
        {
            var openingSheet = workbook.Worksheets.Add("Arrastre inicial");
            openingSheet.Cell(1, 1).Value = "Arrastre seleccionado para el lunes. Las áreas no se suman entre sí.";
            WriteHeaders(openingSheet, 3, 1, ["SKU", "Unidad", "Área", "Cantidad", "Semana origen", "Renglón origen", "Versión origen revisada"]);
            var admitted = await (from o in db.ProductionWeekOpenings.AsNoTracking()
                join p in db.Products on o.ProductId equals p.Id
                join source in db.ProductionScheduleWeeks on o.SourceWeekId equals source.Id
                where o.WeekId == week.Id && o.Quantity > 0
                orderby p.Sku, source.WeekStart, o.Area
                select new { p.Sku, Unit = p.BaseUnit.Code, o.Area, o.Quantity, source.WeekStart, o.SourceLineId, o.SourceFingerprint }).ToListAsync(token);
            var index = 4;
            foreach (var o in admitted)
            {
                openingSheet.Cell(index, 1).Value = Safe(o.Sku); openingSheet.Cell(index, 2).Value = Safe(o.Unit);
                openingSheet.Cell(index, 3).Value = Area(o.Area); openingSheet.Cell(index, 4).Value = o.Quantity;
                openingSheet.Cell(index, 5).Value = o.WeekStart.ToString("yyyy-MM-dd");
                openingSheet.Cell(index, 6).Value = o.SourceLineId.ToString(); openingSheet.Cell(index++, 7).Value = o.SourceFingerprint;
            }
            openingSheet.SheetView.FreezeRows(3); openingSheet.ColumnsUsed().AdjustToContents(12, 48);
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task WritePlanSummaryAsync(XLWorkbook workbook, ProductionScheduleWeek week, CancellationToken token)
    {
        var intentions = await db.ProductionCarryoverPlans.AsNoTracking().Where(x => x.WeekId == week.Id && x.Quantity > 0 && !week.ExplicitCarryover)
            .ToArrayAsync(token);
        var productIds = intentions.Select(x => x.ProductId).Distinct().ToArray();
        var carryUnits = await db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id))
            .Select(x => new { x.Id, x.BaseUnit.Code }).ToDictionaryAsync(x => x.Id, x => x.Code, token);
        var units = week.Lines.Where(x => !x.IsCancelled && !x.IsExtra).Select(x => x.Product.BaseUnit.Code)
            .Concat(carryUnits.Values).Distinct().Order().ToArray();
        if (units.Length == 0) units = ["—"];
        var sheet = workbook.Worksheets.Add("Resumen del programa");
        sheet.Cell(1, 1).Value = "RESUMEN DEL PROGRAMA";
        var headers = new[] { "Día", "Unidad", "Programado nuevo", "Cliente", "Inventario", "Líneas nuevas", "Líneas cliente", "Líneas inventario", "Apertura importada", "Arrastre programado" };
        WriteHeaders(sheet, 3, 1, headers);
        var row = 4;
        foreach (var day in ProductionWeekCalendar.Days(week.WeekStart))
            foreach (var unit in units)
            {
                var lines = week.Lines.Where(x => !x.IsCancelled && !x.IsExtra && x.PlannedDate == day && x.Product.BaseUnit.Code == unit).ToArray();
                var fresh = lines.Where(x => !x.IsCarryover).ToArray();
                var customer = fresh.Where(x => !string.IsNullOrWhiteSpace(x.OrderReference1) || !string.IsNullOrWhiteSpace(x.OrderReference2) || !string.IsNullOrWhiteSpace(x.OrderReference3)).ToArray();
                sheet.Cell(row, 1).Value = day.ToDateTime(TimeOnly.MinValue);
                sheet.Cell(row, 1).Style.DateFormat.Format = "yyyy-MM-dd";
                sheet.Cell(row, 2).Value = unit;
                sheet.Cell(row, 3).Value = fresh.Sum(x => x.Quantity);
                sheet.Cell(row, 4).Value = customer.Sum(x => x.Quantity);
                sheet.Cell(row, 5).Value = fresh.Sum(x => x.Quantity) - customer.Sum(x => x.Quantity);
                sheet.Cell(row, 6).Value = fresh.Length;
                sheet.Cell(row, 7).Value = customer.Length;
                sheet.Cell(row, 8).Value = fresh.Length - customer.Length;
                sheet.Cell(row, 9).Value = lines.Where(x => x.IsCarryover).Sum(x => x.Quantity);
                sheet.Cell(row, 10).Value = intentions.Where(x => x.PlannedDate == day && carryUnits.GetValueOrDefault(x.ProductId) == unit).Sum(x => x.Quantity);
                row++;
            }
        sheet.SheetView.FreezeRows(3);
        sheet.ColumnsUsed().AdjustToContents(10, 38);
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
            headers.AddRange(new[] { "Opening", "Completed today", "Closing pending", summary.ExplicitCarryover ? "Cumulative extra" : "Extra today", "To reconcile", summary.ExplicitCarryover ? "Admitted opening" : "Scheduled carryover (intention)", "T1", "T2", "Pending for T2" }.Select(x => $"{Area(area)} {x}"));
        headers.Add("Status %");
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
            if (product.StatusRatio is decimal ratio)
            {
                sheet.Cell(index, column).Value = ratio;
                sheet.Cell(index, column).Style.NumberFormat.Format = "0.0%";
            }
            else sheet.Cell(index, column).Value = "N/A";
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

    private static void WriteWeekClose(XLWorkbook workbook, ProductionWeekClose close)
    {
        var pending = workbook.Worksheets.Add("Pendiente proxima semana");
        pending.Cell(1, 1).Value = "PENDIENTE PARA PRÓXIMA SEMANA";
        pending.Cell(2, 1).Value = close.WeekEnd.ToDateTime(TimeOnly.MinValue);
        pending.Cell(2, 1).Style.DateFormat.Format = "yyyy-MM-dd";
        pending.Cell(2, 3).Value = close.Status == ProductionScheduleWeekStatus.Closed ? "Cierre" : "Provisional";
        var pendingHeaders = new List<string> { "SKU", "Unidad", "Programado nuevo domingo" };
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
            pendingHeaders.AddRange(new[] { "Arrastre", "T1", "Pendiente T2", "T2", "Pendiente final", "Por conciliar" }
                .Select(x => $"{Area(area)} {x}"));
        WriteHeaders(pending, 4, 1, pendingHeaders);
        var row = 5;
        foreach (var product in close.PendingProducts)
        {
            pending.Cell(row, 1).Value = Safe(product.Sku);
            pending.Cell(row, 2).Value = Safe(product.Unit);
            pending.Cell(row, 3).Value = product.SundayPlan;
            var column = 4;
            foreach (var area in product.Areas)
                foreach (var value in new[] { area.Opening, area.Shift1, area.PendingAfterShift1,
                             area.Shift2, area.Pending, area.ToReconcile })
                    WriteAreaQuantity(pending, row, column++, area.Applies, value);
            row++;
        }
        if (row > 5) pending.Range(4, 1, row - 1, pendingHeaders.Count).CreateTable("NextWeekPending");
        row++;
        foreach (var total in close.Totals)
        {
            pending.Cell(row, 1).Value = "Total filtrado";
            pending.Cell(row, 2).Value = Safe(total.Unit);
            pending.Cell(row, 3).Value = total.SundayPlan;
            var column = 4;
            foreach (var area in total.Areas)
                foreach (var value in new[] { area.Opening, area.Shift1, area.PendingAfterShift1,
                             area.Shift2, area.Pending, area.ToReconcile })
                    WriteAreaQuantity(pending, row, column++, area.Applies, value);
            pending.Range(row, 1, row, pendingHeaders.Count).Style.Font.Bold = true;
            row++;
        }
        pending.SheetView.FreezeRows(4);
        pending.SheetView.FreezeColumns(2);
        pending.ColumnsUsed().AdjustToContents(10, 36);

        var partSummary = workbook.Worksheets.Add("Resumen produccion semanal");
        partSummary.Cell(1, 1).Value = "WEEKLY PART NUMBER PRODUCTION SUMMARY";
        partSummary.Cell(2, 1).Value = close.WeekEnd.ToDateTime(TimeOnly.MinValue);
        partSummary.Cell(2, 1).Style.DateFormat.Format = "yyyy-MM-dd";
        partSummary.Cell(2, 3).Value = close.Status == ProductionScheduleWeekStatus.Closed ? "Cierre" : "Provisional";
        var partHeaders = new[] { "SKU", "Unidad", "Programado", "Corte completado",
            "Costura completado", "Ready to Pack completado" };
        WriteHeaders(partSummary, 4, 1, partHeaders);
        row = 5;
        foreach (var product in close.Products)
        {
            partSummary.Cell(row, 1).Value = Safe(product.Sku);
            partSummary.Cell(row, 2).Value = Safe(product.Unit);
            partSummary.Cell(row, 3).Value = product.WeeklyPlan;
            WriteAreaQuantity(partSummary, row, 4, product.Cutting.Applies, product.Cutting.Completed);
            WriteAreaQuantity(partSummary, row, 5, product.Sewing.Applies, product.Sewing.Completed);
            WriteAreaQuantity(partSummary, row, 6, product.ReadyToPack.Applies, product.ReadyToPack.Completed);
            row++;
        }
        if (row > 5) partSummary.Range(4, 1, row - 1, partHeaders.Length).CreateTable("WeeklyPartProduction");
        row++;
        var summaryTotal = close.SummaryTotal;
        partSummary.Cell(row, 1).Value = "Total filtrado";
        partSummary.Range(row, 1, row, 2).Merge();
        partSummary.Cell(row, 3).Value = summaryTotal.WeeklyPlan;
        partSummary.Cell(row, 4).Value = summaryTotal.Cutting;
        partSummary.Cell(row, 5).Value = summaryTotal.Sewing;
        partSummary.Cell(row, 6).Value = summaryTotal.ReadyToPack;
        partSummary.Range(row, 1, row, partHeaders.Length).Style.Font.Bold = true;
        partSummary.SheetView.FreezeRows(4);
        partSummary.SheetView.FreezeColumns(2);
        partSummary.ColumnsUsed().AdjustToContents(10, 36);

        var completion = workbook.Worksheets.Add("Cumplimiento semanal");
        completion.Cell(1, 1).Value = "CUMPLIMIENTO SEMANAL POR PRODUCTO Y ÁREA";
        completion.Cell(2, 1).Value = close.WeekEnd.ToDateTime(TimeOnly.MinValue);
        completion.Cell(2, 1).Style.DateFormat.Format = "yyyy-MM-dd";
        completion.Cell(2, 3).Value = close.Status == ProductionScheduleWeekStatus.Closed ? "Cierre" : "Provisional";
        var completionHeaders = new List<string> { "SKU", "Unidad", "Programado nuevo semana" };
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
            completionHeaders.AddRange(new[] { $"{Area(area)} completado", $"{Area(area)} cumplimiento %" });
        WriteHeaders(completion, 4, 1, completionHeaders);
        row = 5;
        foreach (var product in close.Products)
        {
            completion.Cell(row, 1).Value = Safe(product.Sku);
            completion.Cell(row, 2).Value = Safe(product.Unit);
            completion.Cell(row, 3).Value = product.WeeklyPlan;
            var column = 4;
            foreach (var area in product.Areas)
            {
                WriteAreaQuantity(completion, row, column++, area.Applies, area.Completed);
                WriteRatio(completion, row, column++, area, product.WeeklyPlan);
            }
            row++;
        }
        if (row > 5) completion.Range(4, 1, row - 1, completionHeaders.Count).CreateTable("WeeklyCompletion");
        row++;
        foreach (var total in close.Totals)
        {
            completion.Cell(row, 1).Value = "Total filtrado";
            completion.Cell(row, 2).Value = Safe(total.Unit);
            completion.Cell(row, 3).Value = total.WeeklyPlan;
            var column = 4;
            foreach (var area in total.Areas)
            {
                WriteAreaQuantity(completion, row, column++, area.Applies, area.Completed);
                WriteRatio(completion, row, column++, area, total.WeeklyPlan);
            }
            completion.Range(row, 1, row, completionHeaders.Count).Style.Font.Bold = true;
            row++;
        }
        completion.SheetView.FreezeRows(4);
        completion.SheetView.FreezeColumns(2);
        completion.ColumnsUsed().AdjustToContents(10, 36);

        var shifts = workbook.Worksheets.Add("Comparacion de turnos");
        shifts.Cell(1, 1).Value = "COMPARACIÓN SEMANAL DE TURNOS";
        shifts.Cell(2, 1).Value = $"{close.WeekStart:yyyy-MM-dd} / {close.WeekEnd:yyyy-MM-dd}";
        shifts.Cell(3, 1).Value = "El total suma trabajo de las áreas; una pieza puede aparecer en varios procesos.";
        WriteHeaders(shifts, 5, 1, ["Unidad", "Área", "T1", "T2", "Total", "% T1", "% T2"]);
        row = 6;
        foreach (var unit in close.ShiftComparison)
        {
            foreach (var area in unit.Areas)
            {
                var areaName = area.Area switch
                {
                    ProductionDailyArea.Cutting => "Corte",
                    ProductionDailyArea.Sewing => "Costura",
                    _ => "Ready to Pack"
                };
                WriteShiftComparisonRow(shifts, row++, unit.Unit, areaName, area.Shift1, area.Shift2);
            }
            WriteShiftComparisonRow(shifts, row, unit.Unit, "Total", unit.Shift1, unit.Shift2);
            shifts.Range(row, 1, row, 7).Style.Font.Bold = true;
            row++;
        }
        shifts.SheetView.FreezeRows(5);
        shifts.ColumnsUsed().AdjustToContents(10, 60);
    }

    private static void WriteShiftComparisonRow(IXLWorksheet sheet, int row, string unit, string area,
        decimal shift1, decimal shift2)
    {
        sheet.Cell(row, 1).Value = Safe(unit);
        sheet.Cell(row, 2).Value = area;
        sheet.Cell(row, 3).Value = shift1;
        sheet.Cell(row, 4).Value = shift2;
        sheet.Cell(row, 5).Value = shift1 + shift2;
        if (shift1 + shift2 == 0)
        {
            sheet.Cell(row, 6).Value = "—";
            sheet.Cell(row, 7).Value = "—";
        }
        else
        {
            sheet.Cell(row, 6).Value = shift1 / (shift1 + shift2);
            sheet.Cell(row, 7).Value = shift2 / (shift1 + shift2);
            sheet.Range(row, 6, row, 7).Style.NumberFormat.Format = "0.0%";
        }
        sheet.Range(row, 3, row, 5).Style.NumberFormat.Format = "0.####";
    }

    private static void WriteAreaQuantity(IXLWorksheet sheet, int row, int column, bool applies, decimal value)
    {
        if (applies) sheet.Cell(row, column).Value = value;
        else sheet.Cell(row, column).Value = "N/A";
        sheet.Cell(row, column).Style.NumberFormat.Format = "0.####";
    }

    private static void WriteRatio(IXLWorksheet sheet, int row, int column, ProductionWeekCloseArea area, decimal planned)
    {
        var ratio = area.CompletionRatio(planned);
        if (ratio.HasValue)
        {
            sheet.Cell(row, column).Value = ratio.Value;
            sheet.Cell(row, column).Style.NumberFormat.Format = "0.0%";
        }
        else sheet.Cell(row, column).Value = area.Applies ? "Sin plan" : "N/A";
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
