using System.IO.Compression;
using ClosedXML.Excel;

namespace WarehouseEPI.Infrastructure.Imports;

public sealed class ProductSpreadsheetReader : IProductSpreadsheetReader
{
    public const int MaxDataRows = 10_000;
    private static readonly (int Column, string Header)[] RequiredHeaders =
    [
        (1, "CLASS"),
        (3, "ITEM (Short)"),
        (4, "DESCRIPTION"),
        (5, "U/M"),
        (12, "COMPLETE PART #")
    ];

    public ProductSpreadsheetReadResult Read(Stream stream)
    {
        try
        {
            using var workbook = new XLWorkbook(stream);
            if (!workbook.TryGetWorksheet("ITEMS", out var worksheet) && !workbook.TryGetWorksheet("ITEM LISTING", out worksheet))
                return Failed("missing_sheet", "El archivo debe contener una hoja llamada ITEMS o ITEM LISTING.");
            var itemListing = worksheet.Name == "ITEM LISTING";

            var issues = new List<ProductSpreadsheetIssue>();
            foreach (var (column, header) in RequiredHeaders)
            {
                var actual = worksheet.Cell(1, column).GetString().Trim();
                var expected = itemListing && column == 12 ? "ITEM (COMPLETE)" : header;
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    issues.Add(new(1, "invalid_header", $"La columna {ColumnName(column)} debe llamarse {expected}.", true));
            }

            if (issues.Any(issue => issue.IsError))
                return new([], issues, 0, 0, 0);

            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > MaxDataRows)
                return Failed("too_many_rows", $"El archivo supera el máximo de {MaxDataRows:N0} filas de datos.");

            return ProductImportRowProcessor.Read(Enumerable.Range(2, Math.Max(0, lastRow - 1))
                .Select(row => new ProductImportInputRow(row, CellText(worksheet, row, 1),
                    CellText(worksheet, row, 3), CellText(worksheet, row, 4),
                    CellText(worksheet, row, 5), CellText(worksheet, row, 12))));
        }
        catch (Exception exception) when (exception is InvalidDataException or FileFormatException or IOException or ArgumentException)
        {
            return Failed("invalid_workbook", "No fue posible leer el archivo como un libro XLSX válido.");
        }
    }

    private static string CellText(IXLWorksheet worksheet, int row, int column) =>
        worksheet.Cell(row, column).GetFormattedString().Trim();

    private static ProductSpreadsheetReadResult Failed(string code, string message) =>
        new([], [new(null, code, message, true)], 0, 0, 0);

    private static string ColumnName(int column)
    {
        var name = string.Empty;
        while (column > 0)
        {
            column--;
            name = (char)('A' + column % 26) + name;
            column /= 26;
        }
        return name;
    }
}
