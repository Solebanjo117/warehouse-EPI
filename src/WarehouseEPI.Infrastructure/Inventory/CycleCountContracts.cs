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

public sealed record CreateCycleCountPlanCommand(Guid ProductId, Guid LocationId, CycleCountFrequency Frequency, DateOnly AnchorDate, Guid ResponsibleUserId);
public sealed record UpdateCycleCountPlanCommand(Guid Id, CycleCountFrequency Frequency, DateOnly AnchorDate, Guid ResponsibleUserId);
public sealed record SetCycleCountPlanActiveCommand(Guid Id, bool IsActive, Guid ResponsibleUserId);
public sealed record ReleaseScheduledCycleCountsCommand(string Pin, IReadOnlyCollection<Guid> PlanIds, Guid OperationId);
public sealed record CycleCountPlanCatalogFilter(string? Search, bool? IsActive, int Page = 1, int PageSize = 25);
public sealed record CycleCountPlanCatalogItem(Guid Id, Guid ProductId, string Sku, string? Description, string UnitCode,
    Guid LocationId, string LocationCode, CycleCountFrequency Frequency, DateOnly AnchorDate, DateOnly NextDueDate,
    bool IsActive, bool IsBlocked, bool IsInCampaign);
public sealed record CycleCountCalendarFilter(DateOnly From, DateOnly To, DateOnly Today, bool Overdue = false, int Page = 1, int PageSize = 25);
public sealed record CycleCountCalendarItem(string RowKey, Guid PlanId, Guid? CampaignId, DateOnly ScheduledFor,
    string Sku, string? Description, string UnitCode, string LocationCode, CycleCountFrequency Frequency,
    bool IsHistorical, bool IsActive, bool IsBlocked, bool IsInCampaign, CycleCountLocationStatus? LocationStatus,
    decimal? CompletedQuantity, DateTimeOffset? CompletedAt);
public sealed record CycleCountPlanEventItem(CycleCountPlanEventType Type, string ResponsibleName,
    CycleCountFrequency? PreviousFrequency, CycleCountFrequency? NewFrequency, DateOnly? PreviousAnchorDate,
    DateOnly? NewAnchorDate, DateOnly? PreviousNextDueDate, DateOnly? NewNextDueDate,
    bool? PreviousIsActive, bool? NewIsActive, DateTimeOffset RecordedAt);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed record SubmitCycleCountCommand(
    Guid AttemptId,
    Guid OperationId,
    string Pin,
    IReadOnlyList<CycleCountQuantityCommand> Entries,
    bool IsLocationEmpty = false);

public sealed record SubmitCycleCountForUserCommand(
    Guid AttemptId,
    Guid OperationId,
    Guid ResponsibleUserId,
    IReadOnlyList<CycleCountQuantityCommand> Entries,
    bool IsLocationEmpty = false);

public sealed record CycleCountQuantityCommand(Guid ProductId, decimal Quantity, IReadOnlyList<PalletSelection>? PlateCounts = null);

public sealed record CycleCountActionCommand(Guid LocationId, Guid OperationId, string Pin, string? Notes = null,
    IReadOnlyCollection<SharedAssignmentApproval>? ApprovedSharedAssignments = null, Guid? ReviewBatchId = null);

public sealed record CycleCountPreparation(
    Guid CampaignId, Guid CycleCountLocationId, Guid LocationId, string LocationCode,
    DateTimeOffset PreparedAt, IReadOnlyList<CycleCountPreparationEntry> Entries);
public sealed record CycleCountPreparationEntry(Guid ProductId, string Sku, string? Description, string UnitCode,
    bool AllowsDecimals, decimal ExpectedQuantity, uint ExpectedBalanceVersion);
public sealed record SubmitPreparedCycleCountCommand(CycleCountPreparation Preparation, Guid OperationId, string Pin,
    IReadOnlyList<CycleCountQuantityCommand> Entries, bool IsLocationEmpty = false);

public sealed record SubmitPreparedCycleCountForUserCommand(CycleCountPreparation Preparation, Guid OperationId,
    Guid ResponsibleUserId, IReadOnlyList<CycleCountQuantityCommand> Entries, bool IsLocationEmpty = false);

public enum CycleCountReviewDecision { Approve, Recount }
public sealed record CycleCountReviewDecisionCommand(Guid LocationId, Guid OperationId, CycleCountReviewDecision Decision,
    CycleCountAdjustmentReason? Reason = null, string? Notes = null,
    IReadOnlyCollection<SharedAssignmentApproval>? ApprovedSharedAssignments = null);
public sealed record ApproveCycleCountBatchCommand(Guid CampaignId, Guid OperationId, string Pin,
    IReadOnlyList<CycleCountReviewDecisionCommand> Decisions);
public sealed record CycleCountBatchItemResult(Guid LocationId, CycleCountStatus Status, Guid? MovementId = null,
    IReadOnlyList<string>? Errors = null, IReadOnlyList<SharedLocationConflict>? SharingConflicts = null);
public sealed record CycleCountBatchResult(CycleCountStatus Status, Guid? ReviewBatchId,
    IReadOnlyList<CycleCountBatchItemResult> Items, IReadOnlyList<string>? Errors = null);

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
    Guid? PlanId = null,
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
    decimal? CountedQuantity, decimal? ExpectedQuantity, decimal? Difference, bool IsUnexpectedProduct, IReadOnlyList<PalletSelection>? PlateCounts = null, bool HasPlateDifference = false);

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
