using WarehouseEPI.Core;

namespace WarehouseEPI.Infrastructure.Imports;

public sealed record ProductImportInputRow(int RowNumber, string Class, string Sku, string Description, string Unit, string Reference,
    bool UnitRequiresResolution = false);

// Shared normalization, validation and consolidation for every product source.
public static class ProductImportRowProcessor
{
    public static ProductSpreadsheetReadResult Read(IEnumerable<ProductImportInputRow> inputs)
    {
        var issues = new List<ProductSpreadsheetIssue>();
        var rawRows = new List<ProductSpreadsheetRow>();
        var invalidSkus = new HashSet<string>(StringComparer.Ordinal);
        var sourceRows = 0;
        var missingReferences = 0;
        foreach (var input in inputs)
        {
            var rowNumber = input.RowNumber;
            var classValue = input.Class;
            var skuValue = input.Sku;
            var descriptionValue = input.Description;
            var unitValue = input.Unit;
            var referenceValue = input.Reference;

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
                : input.UnitRequiresResolution ? null : ParseUnitCode(unitValue);

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
                    input.UnitRequiresResolution
                        ? $"Selecciona una unidad del catálogo para '{unitValue}'."
                        : $"U/M debe terminar con un código entre paréntesis. Valor recibido: '{unitValue}'.", true));
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

    private static string? ParseUnitCode(string value)
    {
        var trimmed = value.Trim();
        var close = trimmed.LastIndexOf(')');
        var open = trimmed.LastIndexOf('(');
        if (open < 0 || close != trimmed.Length - 1 || close <= open + 1)
            return null;
        return CatalogNormalization.NormalizeCode(trimmed[(open + 1)..close]);
    }

}
