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

            var rawRows = new List<ProductSpreadsheetRow>();
            var invalidSkus = new HashSet<string>(StringComparer.Ordinal);
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
                    unitCode = unitValue;
                }
                if (classCode is null)
                    issues.Add(new(rowNumber, "missing_class", "La clase está vacía y se importará sin clase.", false));
                if (externalReference is null)
                    missingReferences++;

                if (!rowHasError)
                    rawRows.Add(new([rowNumber], sku, description, externalReference, unitCode!, classCode, false) { UnitWasBlank = string.IsNullOrWhiteSpace(unitValue) });
                else if (!string.IsNullOrEmpty(sku))
                    invalidSkus.Add(sku);
            }

            var rows = new List<ProductSpreadsheetRow>();
            var conflicts = new List<ProductSpreadsheetConflict>();
            var consolidatedGroups = 0;
            foreach (var group in rawRows.GroupBy(row => row.Sku, StringComparer.Ordinal))
            {
                var values = group.ToList();
                if (invalidSkus.Contains(group.Key))
                {
                    issues.Add(new(values[0].SourceRows[0], "duplicate_invalid",
                        $"El SKU {group.Key} tiene otra fila inválida; se omite el grupo completo.", true));
                    continue;
                }
                if (values.Count == 1)
                {
                    rows.Add(values[0]);
                    continue;
                }

                var first = values[0];
                var references = values.Select(row => row.ExternalReference).Where(value => value is not null)
                    .Distinct(StringComparer.Ordinal).ToList();
                var descriptions = values.Select(row => row.Description).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                var classes = values.Select(row => row.ClassCode).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                var units = values.Where(row => !row.UnitWasBlank).Select(row => row.UnitCode).Distinct(StringComparer.Ordinal).ToList();
                if (descriptions.Count > 1 || classes.Count > 1 || units.Count > 1 || references.Count > 1)
                {
                    conflicts.Add(new(first.Sku, values));
                    issues.Add(new(values[0].SourceRows[0], "duplicate_conflict",
                        $"El SKU {first.Sku} está repetido con datos contradictorios en las filas {string.Join(", ", values.SelectMany(row => row.SourceRows))}.", true));
                    continue;
                }

                consolidatedGroups++;
                var sourceRowNumbers = values.SelectMany(row => row.SourceRows).OrderBy(row => row).ToList();
                rows.Add(first with
                {
                    SourceRows = sourceRowNumbers,
                    ExternalReference = references.SingleOrDefault(),
                    Description = descriptions.SingleOrDefault(),
                    ClassCode = classes.SingleOrDefault(),
                    UnitCode = units.SingleOrDefault() ?? CatalogDefaults.UnassignedUnitCode,
                    UnitWasBlank = units.Count == 0,
                    IsConsolidated = true
                });
                issues.Add(new(sourceRowNumbers[0], "duplicate_consolidated",
                    $"El SKU {first.Sku} se consolidó desde las filas {string.Join(", ", sourceRowNumbers)}.", false));
            }

            return new(rows, issues, sourceRows, consolidatedGroups, missingReferences) { Conflicts = conflicts };
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
