using System.Globalization;
using ClosedXML.Excel;
using WarehouseEPI.Core;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed record WipExcelContext(string WarehouseName, DateTimeOffset GeneratedAt, string TimeZoneId,
    DateOnly? From, DateOnly? To, string? Search, string Area, string FollowUp, string Order);

/// <summary>Builds the workbook only from the exported population and resolved report context.</summary>
public static class WipExcelExporter
{
    private const string QuantityFormat = "#,##0.####";
    private const string DateFormat = "dd/mm/yyyy hh:mm";
    private static readonly string[] Headers = ["Documento", "Fecha local", "Producto", "Descripción", "Unidad", "WIP",
        "Origen documental", "Entregado", "Uso registrado", "Merma", "A bodega", "A proveedor", "Pendiente documental", "Responsable"];

    public static byte[] Export(IReadOnlyList<WipInventoryRow> rows, WipExcelContext context)
    {
        using var book = new XLWorkbook();
        book.Style.Font.FontName = "Calibri";
        book.Style.Font.FontSize = 11;
        var summary = book.Worksheets.Add("Resumen");
        BuildSummary(summary, rows, context);
        var detail = book.Worksheets.Add("WIP");
        BuildDetail(detail, rows);
        summary.SetTabActive();
        summary.SetTabSelected();
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static void BuildSummary(IXLWorksheet sheet, IReadOnlyList<WipInventoryRow> rows, WipExcelContext context)
    {
        sheet.ShowGridLines = false;
        sheet.Column(1).Width = 29;
        sheet.Column(2).Width = 32;
        sheet.Column(3).Width = 36;
        sheet.Column(4).Width = 25;
        sheet.Range("A1:D1").Merge().Value = "WIP · Resumen documental";
        Header(sheet.Range("A1:D1"));
        sheet.Cell("A1").Style.Font.FontSize = 18;
        sheet.Row(1).Height = 36;
        Metadata(3, "Almacén", context.WarehouseName);
        Metadata(4, "Generado (hora local)", context.GeneratedAt.DateTime);
        sheet.Cell(4, 2).Style.DateFormat.Format = DateFormat;
        Metadata(5, "Zona horaria", context.TimeZoneId);
        Metadata(6, "Documentos exportados", rows.Count);
        Metadata(8, "Desde (inclusive)", context.From?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "Sin límite inicial");
        Metadata(9, "Hasta (inclusive)", context.To?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "Sin límite final");
        Metadata(10, "Búsqueda", string.IsNullOrWhiteSpace(context.Search) ? "Sin búsqueda" : context.Search);
        Metadata(11, "Área WIP", context.Area);
        Metadata(12, "Seguimiento", context.FollowUp);
        Metadata(13, "Orden", context.Order);
        Note(15, "Las fechas filtran la entrega original o la apertura del corte.");
        Note(16, "El pendiente documental no representa existencias físicas ni material disponible.");
        Note(18, "Pendientes por unidad · Solo cantidades positivas de los documentos exportados. UNASSIGNED se separa por producto y SKU; no se suman unidades distintas.");
        sheet.Row(18).Height = 38;
        string[] headers = ["Unidad", "SKU (sin unidad)", "Producto (sin unidad)", "Pendiente documental"];
        for (var c = 0; c < headers.Length; c++) Text(sheet.Cell(20, c + 1), headers[c]);
        Header(sheet.Range("A20:D20"));
        sheet.Row(20).Height = 32;
        var groups = rows.Where(x => x.Quantity > 0).GroupBy(x => new
        {
            x.Unit,
            Product = x.Unit == CatalogDefaults.UnassignedUnitCode ? x.ProductId : (Guid?)null
        }).OrderBy(x => x.Key.Unit, StringComparer.Ordinal).ThenBy(x => x.First().ProductSku, StringComparer.Ordinal).ThenBy(x => x.Key.Product);
        var r = 21;
        foreach (var group in groups)
        {
            Text(sheet.Cell(r, 1), group.Key.Unit);
            if (group.Key.Product is not null)
            {
                Text(sheet.Cell(r, 2), group.First().ProductSku);
                Text(sheet.Cell(r, 3), group.First().ProductDescription);
            }
            sheet.Cell(r, 4).Value = group.Sum(x => x.Quantity);
            sheet.Cell(r, 4).Style.NumberFormat.Format = QuantityFormat;
            sheet.Range(r, 1, r, 4).Style.Fill.BackgroundColor = XLColor.FromHtml(r % 2 == 0 ? "#F1F5F9" : "#FFFFFF");
            sheet.Row(r).Height = Math.Max(TextHeight(group.Key.Unit, 27), group.Key.Product is null ? 26 :
                Math.Max(TextHeight(group.First().ProductSku, 30), TextHeight(group.First().ProductDescription, 34)));
            r++;
        }
        if (r == 21) Note(21, rows.Count == 0 ? "Sin documentos para los filtros aplicados." : "Sin pendiente documental positivo en los documentos exportados.");
        sheet.Range(1, 1, Math.Max(r, 21), 4).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        sheet.Range(1, 1, Math.Max(r, 21), 4).Style.Alignment.WrapText = true;

        void Metadata(int row, string label, object value)
        {
            Text(sheet.Cell(row, 1), label);
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Range(row, 2, row, 4).Merge();
            if (value is DateTime date) sheet.Cell(row, 2).Value = date;
            else if (value is int count) sheet.Cell(row, 2).Value = count;
            else Text(sheet.Cell(row, 2), (string)value);
            sheet.Row(row).Height = TextHeight(Convert.ToString(value, CultureInfo.InvariantCulture), 85);
        }
        void Note(int row, string value)
        {
            sheet.Range(row, 1, row, 4).Merge();
            Text(sheet.Cell(row, 1), value);
            sheet.Row(row).Height = 30;
        }
    }

    private static void BuildDetail(IXLWorksheet sheet, IReadOnlyList<WipInventoryRow> rows)
    {
        double[] widths = [38, 22, 22, 48, 16, 20, 22, 18, 18, 18, 18, 18, 22, 32];
        for (var c = 0; c < Headers.Length; c++)
        {
            Text(sheet.Cell(1, c + 1), Headers[c]);
            sheet.Column(c + 1).Width = widths[c];
        }
        Header(sheet.Range("A1:N1"));
        sheet.Row(1).Height = 34;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var r = index + 2;
            Text(sheet.Cell(r, 1), row.DocumentId.ToString());
            sheet.Cell(r, 2).Value = row.UpdatedAt.DateTime;
            sheet.Cell(r, 2).Style.DateFormat.Format = DateFormat;
            Text(sheet.Cell(r, 3), row.ProductSku);
            Text(sheet.Cell(r, 4), row.ProductDescription);
            Text(sheet.Cell(r, 5), row.Unit);
            Text(sheet.Cell(r, 6), row.WipArea);
            Text(sheet.Cell(r, 7), row.IsOpening ? "Apertura del corte" : "Surtimiento");
            decimal[] quantities = [row.Delivered, row.Used, row.Scrapped, row.WarehouseReturned, row.SupplierReturned, row.Quantity];
            for (var c = 0; c < quantities.Length; c++) sheet.Cell(r, c + 8).Value = quantities[c];
            sheet.Range(r, 8, r, 13).Style.NumberFormat.Format = QuantityFormat;
            Text(sheet.Cell(r, 14), row.Responsible);
            sheet.Range(r, 1, r, 14).Style.Fill.BackgroundColor = XLColor.FromHtml(r % 2 == 0 ? "#F1F5F9" : "#FFFFFF");
            sheet.Cell(r, 13).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF3CD");
            var height = 30d;
            foreach (var c in new[] { 3, 4, 5, 6, 7, 14 })
                height = Math.Max(height, TextHeight(sheet.Cell(r, c).GetString(), (int)widths[c - 1] - 2));
            sheet.Row(r).Height = height;
        }
        var range = sheet.Range(1, 1, rows.Count + 1, 14);
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        range.Style.Alignment.WrapText = true;
        range.SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
    }

    private static double TextHeight(string? value, int width) => Math.Clamp(
        (value ?? "").Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (double)width))) * 16 + 8, 26, 409);

    private static void Header(IXLRange range)
    {
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#17324D");
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
    }

    private static void Text(IXLCell cell, string? value)
    {
        var text = value ?? "";
        var first = text.TrimStart();
        cell.Style.NumberFormat.Format = "@";
        cell.Value = text;
        // Preserve the literal value while explicitly marking suspicious Excel input as quoted text.
        if (text.Length > 0 && (text[0] is '\t' or '\r' or '\n' || first.Length > 0 && first[0] is '=' or '+' or '-' or '@'))
            cell.Style.IncludeQuotePrefix = true;
    }
}
