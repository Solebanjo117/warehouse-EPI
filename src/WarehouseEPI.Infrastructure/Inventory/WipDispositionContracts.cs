using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record WipDispositionCommand(
    Guid OperationId,
    Guid OriginalMovementLineId,
    WipDispositionType Type,
    decimal Quantity,
    string Pin,
    Guid? DestinationLocationId = null,
    string? Reference = null,
    string? Notes = null,
    IReadOnlyCollection<SharedAssignmentApproval>? ApprovedSharedAssignments = null);

public enum WipDispositionStatus
{
    Success,
    InvalidPin,
    ValidationFailed,
    RequiresLocationSharingConfirmation,
    IdempotencyConflict
}

public sealed record WipDispositionResult(
    WipDispositionStatus Status,
    Guid? DispositionId = null,
    Guid? InventoryMovementId = null,
    decimal? RemainingQuantity = null,
    IReadOnlyList<InventoryBalanceResult>? Balances = null,
    IReadOnlyList<SharedLocationConflict>? SharingConflicts = null,
    IReadOnlyList<string>? Errors = null)
{
    public IReadOnlyList<InventoryBalanceResult> ResultingBalances => Balances ?? [];
    public IReadOnlyList<SharedLocationConflict> Conflicts => SharingConflicts ?? [];
    public IReadOnlyList<string> ValidationErrors => Errors ?? [];
}
