using ClosedXML.Excel;

namespace WarehouseEPI.Infrastructure.Production;

public static class ProductionReportExportService
{
    public static byte[] Export(ProductionReport report, DateTimeOffset generatedAt, Func<string, string> translate)
    {
        if (!report.CanOutput) throw new InvalidOperationException("El reporte supera 10.000 filas. Usa filtros más específicos.");
        var table = ProductionReportTable.Create(report);
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Report");
        sheet.Cell(1, 1).Value = translate(report.Filter.Products ? "Avance y pendientes por producto" : "Resumen diario por proceso");
        sheet.Cell(2, 1).Value = $"{report.WeekStart:yyyy-MM-dd} – {report.WeekEnd:yyyy-MM-dd}";
        sheet.Cell(2, 2).Value = report.Filter.Products && !report.Filter.FullWeek ? report.Filter.Date.ToDateTime(TimeOnly.MinValue) : "";
        sheet.Cell(2, 2).Style.DateFormat.Format = "yyyy-MM-dd";
        sheet.Cell(3, 1).Value = $"{translate("Consulta")}: {generatedAt:yyyy-MM-dd HH:mm:ss zzz} · {translate("Solo cantidades guardadas")}";
        sheet.Cell(4, 1).Value = $"{translate("Producto")}: {report.Filter.Sku} · {translate("Referencia")}: {report.Filter.Reference} · {translate("Proceso")}: {(report.Filter.Area is { } area ? translate(ProductionReportTable.AreaName(area)) : translate("Todos"))}";
        sheet.Cell(5, 1).Value = $"T1: {report.Shift1Name} / T2: {report.Shift2Name} · {translate(report.Status == Core.Entities.ProductionScheduleWeekStatus.Closed ? "Semana cerrada" : "Cifras provisionales")}";
        for (var i = 0; i < table.Columns.Count; i++)
        {
            sheet.Cell(7, i + 1).Value = table.Columns[i].Group is string group ? translate(group) : "";
            sheet.Cell(8, i + 1).Value = translate(table.Columns[i].Label);
        }
        var start = 0;
        while (start < table.Columns.Count)
        {
            var end = start;
            while (end + 1 < table.Columns.Count && table.Columns[end + 1].Group == table.Columns[start].Group) end++;
            if (table.Columns[start].Group is not null && end > start) sheet.Range(7, start + 1, 7, end + 1).Merge();
            start = end + 1;
        }
        var rowNumber = 9;
        foreach (var row in table.Rows.Concat(table.Totals))
        {
            for (var i = 0; i < row.Cells.Count; i++)
            {
                var cell = sheet.Cell(rowNumber, i + 1);
                switch (row.Cells[i])
                {
                    case decimal number: cell.Value = number; cell.Style.NumberFormat.Format = "#,##0.####"; break;
                    case DateOnly date: cell.Value = date.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "dd/MM/yyyy"; break;
                    default:
                        var value = (string)row.Cells[i];
                        // Only translate operational labels, never user-entered SKU/description/unit values.
                        var label = value == "No aplica" || (i == 0 && (!report.Filter.Products || rowNumber >= 9 + table.Rows.Count));
                        cell.Value = Safe(label ? translate(value) : value);
                        break;
                }
            }
            rowNumber++;
        }
        sheet.Range(7, 1, 8, table.Columns.Count).Style.Font.Bold = true;
        if (table.Totals.Count > 0) sheet.Range(9 + table.Rows.Count, 1, rowNumber - 1, table.Columns.Count).Style.Font.Bold = true;
        if (table.Rows.Count > 0) sheet.Range(8, 1, 8 + table.Rows.Count, table.Columns.Count).SetAutoFilter();
        sheet.SheetView.FreezeRows(8);
        sheet.Columns(1, table.Columns.Count).Width = 16;
        sheet.Column(1).Width = report.Filter.Products ? 30 : 18;
        sheet.Rows(7, 8).Style.Alignment.WrapText = true;
        sheet.Row(7).Height = 28;
        sheet.Row(8).Height = 44;
        ApplyProcessColors(sheet, table, rowNumber - 1);
        sheet.SheetView.FreezeColumns(1);
        sheet.ShowGridLines = false;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.PaperSize = report.Filter.Products && report.Areas.Count > 1 ? XLPaperSize.A3Paper : XLPaperSize.A4Paper;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(7, 8);
        using var output = new MemoryStream();
        book.SaveAs(output);
        return output.ToArray();
    }

    private static void ApplyProcessColors(IXLWorksheet sheet, ProductionReportTable table, int lastRow)
    {
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 16;
        sheet.Range(7, 1, 8, table.Columns.Count).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Range(7, 1, 8, table.Columns.Count).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        var totalStart = 9 + table.Rows.Count;
        var start = 0;
        while (start < table.Columns.Count)
        {
            var group = table.Columns[start].Group;
            var end = start;
            while (end + 1 < table.Columns.Count && table.Columns[end + 1].Group == group) end++;
            var (header, light, total) = group switch
            {
                "Corte" => ("#245A81", "#EDF5FC", "#CFE2F3"),
                "Costura" => ("#9C4A13", "#FFF5EB", "#FADCC0"),
                "Ready to Pack" => ("#276443", "#EEF7F0", "#CCE6D5"),
                _ => ("#3E4D5E", "#F3F5F7", "#DDE3EA")
            };
            var columns = sheet.Range(7, start + 1, lastRow, end + 1);
            columns.Style.Fill.BackgroundColor = XLColor.FromHtml(light);
            columns.Style.Font.FontColor = XLColor.FromHtml("#202B36");
            columns.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
            columns.Style.Border.LeftBorderColor = XLColor.FromHtml(total);
            columns.Style.Border.RightBorder = XLBorderStyleValues.Thin;
            columns.Style.Border.RightBorderColor = XLColor.FromHtml(total);
            var heading = sheet.Range(7, start + 1, 8, end + 1);
            heading.Style.Fill.BackgroundColor = XLColor.FromHtml(header);
            heading.Style.Font.FontColor = XLColor.White;
            if (table.Totals.Count > 0)
            {
                var totals = sheet.Range(totalStart, start + 1, lastRow, end + 1);
                totals.Style.Fill.BackgroundColor = XLColor.FromHtml(total);
                totals.Style.Border.TopBorder = XLBorderStyleValues.Medium;
                totals.Style.Border.TopBorderColor = XLColor.FromHtml(header);
            }
            sheet.Range(7, start + 1, lastRow, start + 1).Style.Border.LeftBorder = XLBorderStyleValues.Medium;
            sheet.Range(7, start + 1, lastRow, start + 1).Style.Border.LeftBorderColor = XLColor.FromHtml(header);
            start = end + 1;
        }
    }

    private static string Safe(string value) => value.Length > 0 && "=+-@\t\r\n".Contains(value[0]) ? "'" + value : value;
}
