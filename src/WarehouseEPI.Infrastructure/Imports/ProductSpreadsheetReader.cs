using System.IO.Compression;
using ClosedXML.Excel;
using WarehouseEPI.Core;

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
            if (!workbook.TryGetWorksheet("ITEMS", out var worksheet))
                return Failed("missing_sheet", "El archivo debe contener una hoja llamada ITEMS.");

            var issues = new List<ProductSpreadsheetIssue>();
            foreach (var (column, header) in RequiredHeaders)
            {
                var actual = worksheet.Cell(1, column).GetString().Trim();
                if (!string.Equals(actual, header, StringComparison.Ordinal))
                    issues.Add(new(1, "invalid_header", $"La columna {ColumnName(column)} debe llamarse {header}.", true));
            }

            if (issues.Any(issue => issue.IsError))
                return new([], issues, 0, 0, 0);

            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > MaxDataRows)
                return Failed("too_many_rows", $"El archivo supera el máximo de {MaxDataRows:N0} filas de datos.");

            var rawRows = new List<ProductSpreadsheetRow>();
            var sourceRows = 0;
            var missingReferences = 0;
            for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
            {
                var classValue = CellText(worksheet, rowNumber, 1);
                var skuValue = CellText(worksheet, rowNumber, 3);
                var descriptionValue = CellText(worksheet, rowNumber, 4);
                var unitValue = CellText(worksheet, rowNumber, 5);
                var referenceValue = CellText(worksheet, rowNumber, 12);

                if (string.IsNullOrWhiteSpace(classValue) && string.IsNullOrWhiteSpace(skuValue) &&
                    string.IsNullOrWhiteSpace(descriptionValue) && string.IsNullOrWhiteSpace(unitValue) &&
                    string.IsNullOrWhiteSpace(referenceValue))
                    continue;

                sourceRows++;
                var sku = CatalogNormalization.NormalizeCode(skuValue);
                var description = CatalogNormalization.NormalizeOptional(descriptionValue);
                var externalReference = CatalogNormalization.NormalizeOptional(referenceValue);
                var classCode = CatalogNormalization.NormalizeOptional(classValue) is { } normalizedClass
                    ? CatalogNormalization.NormalizeCode(normalizedClass)
                    : null;
                var unitCode = string.IsNullOrWhiteSpace(unitValue)
                    ? CatalogDefaults.UnassignedUnitCode
                    : ParseUnitCode(unitValue);

                var rowHasError = false;
                if (string.IsNullOrEmpty(sku))
                {
                    issues.Add(new(rowNumber, "missing_sku", "El SKU es obligatorio.", true));
                    rowHasError = true;
                }
                else if (sku.Length > 60)
                {
                    issues.Add(new(rowNumber, "sku_too_long", "El SKU supera 60 caracteres.", true));
                    rowHasError = true;
                }

                if (externalReference?.Length > 120)
                {
                    issues.Add(new(rowNumber, "reference_too_long", "La referencia externa supera 120 caracteres.", true));
                    rowHasError = true;
                }
                if (string.IsNullOrWhiteSpace(unitValue))
                {
                    issues.Add(new(rowNumber, "missing_unit_defaulted",
                        "U/M está vacía y se importará con la unidad Sin asignar.", false));
                }
                else if (unitCode is null)
                {
                    issues.Add(new(rowNumber, "invalid_unit",
                        $"U/M debe terminar con un código entre paréntesis. Valor recibido: '{unitValue}'.", true));
                    rowHasError = true;
                }
                if (classCode is null)
                    issues.Add(new(rowNumber, "missing_class", "La clase está vacía y se importará sin clase.", false));
                if (externalReference is null)
                    missingReferences++;

                if (!rowHasError)
                    rawRows.Add(new([rowNumber], sku, description, externalReference, unitCode!, classCode, false));
            }

            var rows = new List<ProductSpreadsheetRow>();
            var consolidatedGroups = 0;
            foreach (var group in rawRows.GroupBy(row => row.Sku, StringComparer.Ordinal))
            {
                var values = group.ToList();
                if (values.Count == 1)
                {
                    rows.Add(values[0]);
                    continue;
                }

                var first = values[0];
                var references = values.Select(row => row.ExternalReference).Where(value => value is not null)
                    .Distinct(StringComparer.Ordinal).ToList();
                var coreMatches = values.All(row =>
                    string.Equals(row.Description, first.Description, StringComparison.Ordinal) &&
                    string.Equals(row.UnitCode, first.UnitCode, StringComparison.Ordinal) &&
                    string.Equals(row.ClassCode, first.ClassCode, StringComparison.Ordinal));

                if (!coreMatches || references.Count > 1)
                {
                    issues.Add(new(values[0].SourceRows[0], "duplicate_conflict",
                        $"El SKU {first.Sku} está repetido con datos contradictorios.", true));
                    continue;
                }

                consolidatedGroups++;
                var sourceRowNumbers = values.SelectMany(row => row.SourceRows).OrderBy(row => row).ToList();
                rows.Add(first with
                {
                    SourceRows = sourceRowNumbers,
                    ExternalReference = references.SingleOrDefault(),
                    IsConsolidated = true
                });
                issues.Add(new(sourceRowNumbers[0], "duplicate_consolidated",
                    $"El SKU {first.Sku} se consolidó desde las filas {string.Join(", ", sourceRowNumbers)}.", false));
            }

            return new(rows, issues, sourceRows, consolidatedGroups, missingReferences);
        }
        catch (Exception exception) when (exception is InvalidDataException or FileFormatException or IOException or ArgumentException)
        {
            return Failed("invalid_workbook", "No fue posible leer el archivo como un libro XLSX válido.");
        }
    }

    private static string CellText(IXLWorksheet worksheet, int row, int column) =>
        worksheet.Cell(row, column).GetFormattedString().Trim();

    private static string? ParseUnitCode(string value)
    {
        var trimmed = value.Trim();
        var close = trimmed.LastIndexOf(')');
        var open = trimmed.LastIndexOf('(');
        if (open < 0 || close != trimmed.Length - 1 || close <= open + 1)
            return null;
        return CatalogNormalization.NormalizeCode(trimmed[(open + 1)..close]);
    }

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
