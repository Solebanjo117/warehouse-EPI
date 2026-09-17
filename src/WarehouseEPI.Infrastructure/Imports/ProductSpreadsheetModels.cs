namespace WarehouseEPI.Infrastructure.Imports;

public sealed record ProductSpreadsheetIssue(int? RowNumber, string Code, string Message, bool IsError);

public sealed record ProductSpreadsheetRow(
    IReadOnlyList<int> SourceRows,
    string Sku,
    string? Description,
    string? ExternalReference,
    string UnitCode,
    string? ClassCode,
    bool IsConsolidated)
{
    public bool UnitWasBlank { get; init; }
}

public sealed record ProductSpreadsheetReadResult(
    IReadOnlyList<ProductSpreadsheetRow> Rows,
    IReadOnlyList<ProductSpreadsheetIssue> Issues,
    int SourceRowCount,
    int ConsolidatedGroupCount,
    int MissingExternalReferenceCount)
{
    public bool HasErrors => Issues.Any(issue => issue.IsError);
    public IReadOnlyList<ProductSpreadsheetConflict> Conflicts { get; init; } = [];
}

public sealed record ProductSpreadsheetConflict(string Sku, IReadOnlyList<ProductSpreadsheetRow> Rows)
{
    public IReadOnlyDictionary<string, string[]> Fields => new Dictionary<string, string[]>
    {
        ["Descripción"] = Rows.Select(x => x.Description).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
        ["Referencia completa"] = Rows.Select(x => x.ExternalReference).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
        ["Clase"] = Rows.Select(x => x.ClassCode).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
        ["Unidad"] = Rows.Where(x => !x.UnitWasBlank).Select(x => x.UnitCode).Distinct(StringComparer.Ordinal).ToArray()
    };
}

public interface IProductSpreadsheetReader
{
    ProductSpreadsheetReadResult Read(Stream stream);
}
