using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Infrastructure.Catalogs;

public sealed class ProductCatalogExportService(WarehouseDbContext db)
{
    public const int MaxRows = 10_000;
    public const string LimitMessage = "El catálogo supera el límite de 10,000 productos por archivo. No se exportó una lista parcial.";

    public async Task<byte[]> ExportAsync(CancellationToken token = default)
    {
        // Deliberately exports the whole catalogue, independent of list filters and pagination.
        var products = await db.Products.AsNoTracking().OrderBy(x => x.Sku).ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Sku, x.Description, x.ExternalReference,
                Class = x.ProductClass == null ? null : x.ProductClass.Code,
                UnitCode = x.BaseUnit.Code, UnitName = x.BaseUnit.Name
            }).Take(MaxRows + 1).ToListAsync(token);
        if (products.Count > MaxRows) throw new InvalidOperationException(LimitMessage);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("ITEM LISTING");
        string[] headers = ["CLASS", "PACKED BY (AREA)", "ITEM (Short)", "DESCRIPTION", "U/M", "ITEM (COMPLETE)"];
        for (var column = 0; column < headers.Length; column++) Text(sheet.Cell(1, column + 1), headers[column]);
        for (var index = 0; index < products.Count; index++)
        {
            var product = products[index];
            var row = index + 2;
            Text(sheet.Cell(row, 1), product.Class);
            // PACKED BY (AREA) is not persisted by the product importer or product model.
            Text(sheet.Cell(row, 2), null);
            Text(sheet.Cell(row, 3), product.Sku);
            Text(sheet.Cell(row, 4), product.Description);
            Text(sheet.Cell(row, 5), product.UnitCode == CatalogDefaults.UnassignedUnitCode
                ? null : $"{product.UnitName} ({product.UnitCode})");
            Text(sheet.Cell(row, 6), product.ExternalReference);
        }
        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#173D50");
        header.Style.Alignment.WrapText = true;
        sheet.Row(1).Height = 32;
        double[] widths = [20, 25, 36, 66, 30, 48];
        for (var column = 0; column < widths.Length; column++) sheet.Column(column + 1).Width = widths[column];
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, products.Count + 1, headers.Length).SetAutoFilter();
        if (products.Count > 0)
        {
            var body = sheet.Range(2, 1, products.Count + 1, headers.Length);
            body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            body.Style.Alignment.WrapText = true;
            sheet.Rows(2, products.Count + 1).Height = 32;
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Text(IXLCell cell, string? value)
    {
        // SetValue(string) stores text; quote-prefix protection does not alter the original SKU/value.
        var text = value ?? string.Empty;
        // ClosedXML consumes one leading apostrophe as Excel's text marker.
        // Escape that marker once so a literal apostrophe in the source survives.
        cell.SetValue(text.StartsWith('\'') ? "'" + text : text);
        cell.Style.NumberFormat.Format = "@";
        if (ReportExportService.SanitizeText(text) != text) cell.Style.IncludeQuotePrefix = true;
    }
}
