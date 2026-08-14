using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record InventoryMovementCommand(
    Guid OperationId,
    InventoryMovementType Type,
    string Pin,
    IReadOnlyList<InventoryMovementLineCommand> Lines,
    string? Reference = null,
    string? Notes = null,
    IReadOnlyCollection<SharedAssignmentApproval>? ApprovedSharedAssignments = null);

public sealed record InventoryMovementLineCommand(
    Guid ProductId,
    decimal Quantity,
    Guid? SourceLocationId = null,
    Guid? DestinationLocationId = null,
    Guid? LocationId = null,
    uint? ExpectedBalanceVersion = null,
    Guid? LotId = null);

public sealed record SharedAssignmentApproval(Guid ProductId, Guid LocationId);

public enum InventoryMovementStatus
{
    Success,
    InvalidPin,
    ValidationFailed,
    RequiresLocationSharingConfirmation,
    BalanceChanged,
    LotSupportPending,
    IdempotencyConflict
}

public sealed record SharedLocationConflict(
    Guid ProductId,
    string ProductSku,
    Guid LocationId,
    string LocationCode,
    IReadOnlyList<string> ExistingProductSkus);

public sealed record InventoryBalanceResult(
    Guid ProductId,
    Guid LocationId,
    Guid? LotId,
    decimal Quantity,
    uint Version,
    bool IsNegative);

public sealed record InventoryMovementResult(
    InventoryMovementStatus Status,
    Guid? MovementId = null,
    Guid? ResponsibleUserId = null,
    string? ResponsibleName = null,
    IReadOnlyList<InventoryBalanceResult>? Balances = null,
    IReadOnlyList<SharedLocationConflict>? SharingConflicts = null,
    IReadOnlyList<string>? Errors = null)
{
    public IReadOnlyList<InventoryBalanceResult> ResultingBalances => Balances ?? [];
    public IReadOnlyList<SharedLocationConflict> Conflicts => SharingConflicts ?? [];
    public IReadOnlyList<string> ValidationErrors => Errors ?? [];
    public bool HasNegativeBalance => ResultingBalances.Any(balance => balance.IsNegative);
}
