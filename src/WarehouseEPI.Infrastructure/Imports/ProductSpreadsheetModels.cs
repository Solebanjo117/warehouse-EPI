namespace WarehouseEPI.Infrastructure.Imports;

public sealed record ProductSpreadsheetIssue(int? RowNumber, string Code, string Message, bool IsError);

public sealed record ProductSpreadsheetRow(
    IReadOnlyList<int> SourceRows,
    string Sku,
    string? Description,
    string? ExternalReference,
    string UnitCode,
    string? ClassCode,
    bool IsConsolidated);

public sealed record ProductSpreadsheetReadResult(
    IReadOnlyList<ProductSpreadsheetRow> Rows,
    IReadOnlyList<ProductSpreadsheetIssue> Issues,
    int SourceRowCount,
    int ConsolidatedGroupCount,
    int MissingExternalReferenceCount)
{
    public bool HasErrors => Issues.Any(issue => issue.IsError);
}

public interface IProductSpreadsheetReader
{
    ProductSpreadsheetReadResult Read(Stream stream);
}
