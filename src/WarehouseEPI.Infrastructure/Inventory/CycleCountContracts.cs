using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record CreateCycleCountCommand(
    string Pin,
    string? Title,
    string? Notes,
    IReadOnlyCollection<Guid>? LocationIds = null,
    IReadOnlyCollection<string>? RowCodes = null,
    IReadOnlyCollection<short>? RackNumbers = null,
    Guid OperationId = default);

public sealed record SubmitCycleCountCommand(
    Guid AttemptId,
    Guid OperationId,
    string Pin,
    IReadOnlyList<CycleCountQuantityCommand> Entries,
    bool IsLocationEmpty = false);

public sealed record CycleCountQuantityCommand(Guid ProductId, decimal Quantity);

public sealed record CycleCountActionCommand(Guid LocationId, Guid OperationId, string Pin, string? Notes = null,
    IReadOnlyCollection<SharedAssignmentApproval>? ApprovedSharedAssignments = null);

public enum CycleCountStatus
{
    Success,
    InvalidPin,
    ValidationFailed,
    NotFound,
    InvalidState,
    BalanceChanged,
    RequiresLocationSharingConfirmation,
    IdempotencyConflict
}

public sealed record CycleCountResult(
    CycleCountStatus Status,
    Guid? CampaignId = null,
    Guid? LocationId = null,
    Guid? AttemptId = null,
    Guid? MovementId = null,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<SharedLocationConflict>? SharingConflicts = null)
{
    public IReadOnlyList<string> ValidationErrors => Errors ?? [];
    public IReadOnlyList<SharedLocationConflict> Conflicts => SharingConflicts ?? [];
}

public sealed record CycleCountCampaignListItem(
    Guid Id, string Folio, string? Title, CycleCountCampaignStatus Status,
    DateTimeOffset CreatedAt, int LocationCount, int CompletedLocationCount, int DifferenceLocationCount);

public sealed record CycleCountLocationItem(
    Guid Id, Guid LocationId, string LocationCode, string? LocationDescription,
    string? RowCode, short? RackNumber, CycleCountLocationStatus Status, int AttemptCount,
    Guid? AdjustmentMovementId, Guid? ActiveAttemptId);

public sealed record CycleCountEntryItem(
    Guid ProductId, string Sku, string? Description, string UnitCode, bool AllowsDecimals,
    decimal? CountedQuantity, decimal? ExpectedQuantity, decimal? Difference, bool IsUnexpectedProduct);

public sealed record CycleCountAttemptView(
    Guid Id, int AttemptNumber, CycleCountAttemptStatus Status, DateTimeOffset StartedAt,
    string StartedByName, DateTimeOffset? SubmittedAt, string? SubmittedByName,
    IReadOnlyList<CycleCountEntryItem> Entries);

public sealed record CycleCountCampaignDetail(
    Guid Id, string Folio, string? Title, string? Notes, CycleCountCampaignStatus Status,
    DateTimeOffset CreatedAt, string CreatedByName, IReadOnlyList<CycleCountLocationItem> Locations);

public sealed record CycleCountExportRow(
    string Folio, string LocationCode, int AttemptNumber, string Sku, string? Description, string UnitCode,
    decimal ExpectedQuantity, decimal? CountedQuantity, decimal? Difference, bool IsUnexpectedProduct,
    CycleCountLocationStatus LocationStatus, DateTimeOffset StartedAt, DateTimeOffset? SubmittedAt);
