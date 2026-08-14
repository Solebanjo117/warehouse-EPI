using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record InventoryReplacementCommand(
    InventoryMovementType Type,
    IReadOnlyList<InventoryMovementLineCommand> Lines,
    string? Reference = null,
    string? Notes = null,
    IReadOnlyCollection<SharedAssignmentApproval>? ApprovedSharedAssignments = null);

public sealed record InventoryCorrectionCommand(
    Guid OperationId,
    Guid OriginalMovementId,
    Guid RequestedByUserId,
    string Pin,
    string Reason,
    InventoryReplacementCommand? Replacement = null);

public enum InventoryCorrectionStatus
{
    Success,
    InvalidPin,
    Unauthorized,
    ValidationFailed,
    AlreadyCorrected,
    CannotCorrectReversal,
    RequiresLocationSharingConfirmation,
    BalanceChanged,
    IdempotencyConflict
}

public sealed record InventoryCorrectionResult(
    InventoryCorrectionStatus Status,
    Guid? CorrectionId = null,
    Guid? ReversalMovementId = null,
    Guid? ReplacementMovementId = null,
    IReadOnlyList<SharedLocationConflict>? SharingConflicts = null,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<InventoryBalanceResult>? Balances = null)
{
    public IReadOnlyList<SharedLocationConflict> Conflicts => SharingConflicts ?? [];
    public IReadOnlyList<string> ValidationErrors => Errors ?? [];
    public IReadOnlyList<InventoryBalanceResult> ResultingBalances => Balances ?? [];
    public bool HasNegativeBalance => ResultingBalances.Any(balance => balance.IsNegative);
}
