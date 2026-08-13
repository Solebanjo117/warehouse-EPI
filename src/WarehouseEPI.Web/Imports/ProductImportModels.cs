using WarehouseEPI.Infrastructure.Imports;

namespace WarehouseEPI.Web.Imports;

public static class ProductImportLimits
{
    public const long MaxFileBytes = 10L * 1024 * 1024;
    public const long MaxRequestBytes = MaxFileBytes + 64 * 1024;
}

public sealed record ProductImportPreviewRow(
    IReadOnlyList<int> SourceRows,
    string Sku,
    string? Description,
    string? ExternalReference,
    string UnitCode,
    string? ClassCode,
    bool IsExisting,
    bool IsConsolidated,
    bool HasWarning,
    bool HasError,
    string? Message)
{
    public bool IsCandidate => !IsExisting && !HasError;
    public string UnitDisplay => string.Equals(UnitCode,
        WarehouseEPI.Core.CatalogDefaults.UnassignedUnitCode,
        StringComparison.Ordinal) ? "Sin asignar" : UnitCode;
}

public sealed record ProductImportPreview(
    string Token,
    Guid OwnerUserId,
    string FileName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<ProductImportPreviewRow> Rows,
    IReadOnlyList<ProductSpreadsheetIssue> Issues,
    int SourceRowCount,
    int ConsolidatedCount,
    int MissingExternalReferenceCount)
{
    public int NewCount => Rows.Count(row => row.IsCandidate);
    public int ExistingCount => Rows.Count(row => row.IsExisting);
    public int WarningCount => Issues.Count(issue => !issue.IsError);
    public int ErrorCount => Issues.Count(issue => issue.IsError) + Rows.Count(row => row.HasError);
    public bool CanConfirm => ErrorCount == 0;
}

public sealed record ProductImportConfirmation(
    bool Succeeded,
    int Inserted,
    int SkippedExisting,
    int Consolidated,
    string? ErrorMessage = null);
