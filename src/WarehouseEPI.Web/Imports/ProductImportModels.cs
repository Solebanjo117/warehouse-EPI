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
    public ProductImportValues? Current { get; init; }
    public ProductImportValues? Proposed { get; init; }
    public IReadOnlyList<ProductImportChange> Changes { get; init; } = [];
    public bool IsUpdate => IsExisting && !HasError && Changes.Count > 0;
    public bool RequireActiveUnit { get; init; }
    public bool RequireActiveClass { get; init; }
    public bool IsNewClass { get; init; }
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
    public bool UpdateExisting { get; init; }
    public ProductSpreadsheetReadResult? Source { get; init; }
    public IReadOnlyList<ProductImportUnitOption> UnitOptions { get; init; } = [];
    public IReadOnlyList<string> UnresolvedUnits { get; init; } = [];
    public int UpdatedCount => Rows.Count(row => row.IsUpdate);
    public int UnchangedCount => Rows.Count(row => row.IsExisting && !row.HasError && !row.IsUpdate);
    public int NewCount => Rows.Count(row => row.IsCandidate);
    public int ExistingCount => Rows.Count(row => row.IsExisting);
    public int WarningCount => Issues.Count(issue => !issue.IsError);
    public int ErrorCount => Issues.Count(issue => issue.IsError) + Rows.Count(row => row.HasError);
    public bool CanConfirm => UpdateExisting
        ? !Issues.Any(issue => issue.IsError && (issue.RowNumber is null || issue.Code == "invalid_header")) && Rows.Any(row => row.IsCandidate || row.IsUpdate)
        : ErrorCount == 0;
}

public sealed record ProductImportConfirmation(
    bool Succeeded,
    int Inserted,
    int SkippedExisting,
    int Consolidated,
    string? ErrorMessage = null)
{
    public int Updated { get; init; }
}

public sealed record ProductImportChange(string Field, string? Before, string? After);
public sealed record ProductImportUnitOption(string Code, string Name);
public sealed record ProductImportValues(Guid Id, string? Description, string? Reference, short UnitId, short? ClassId, DateTimeOffset UpdatedAt);
