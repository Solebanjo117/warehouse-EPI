using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record InventoryMovementCommand(
    Guid OperationId,
    InventoryMovementType Type,
    string Pin,
    IReadOnlyList<InventoryMovementLineCommand> Lines,
    string? Reference = null,
    string? Notes = null,
    IReadOnlyCollection<SharedAssignmentApproval>? ApprovedSharedAssignments = null,
    InventoryMovementPurpose Purpose = InventoryMovementPurpose.Standard,
    Guid? OperationalAreaId = null);

public sealed record InventoryMovementLineCommand(
    Guid ProductId,
    decimal Quantity,
    Guid? SourceLocationId = null,
    Guid? DestinationLocationId = null,
    Guid? LocationId = null,
    uint? ExpectedBalanceVersion = null,
    IReadOnlyList<InventoryLotSelection>? Lots = null,
    Guid? DestinationLotId = null,
    IReadOnlyList<PalletSelection>? Plates = null,
    Guid? DestinationPlateId = null,
    long? ExpectedDestinationPlateVersion = null,
    IReadOnlyList<decimal>? PalletQuantities = null,
    IReadOnlyList<PalletSelection>? PlateCounts = null,
    Guid? MaterialIssueLinkId = null,
    bool AutomaticPalletHandling = false);

public sealed record PalletSelection(Guid PlateId, decimal Quantity, long ExpectedVersion);
public sealed record PalletMovementResult(Guid PlateId, string Identifier, Guid LocationId, decimal Quantity, long Version, string Status);

public sealed record InventoryLotSelection(Guid LotId, decimal Quantity);

public sealed record SharedAssignmentApproval(Guid ProductId, Guid LocationId);

public enum InventoryMovementStatus
{
    Success,
    InvalidPin,
    ValidationFailed,
    RequiresLocationSharingConfirmation,
    BalanceChanged,
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
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<PalletMovementResult>? Plates = null)
{
    public IReadOnlyList<InventoryBalanceResult> ResultingBalances => Balances ?? [];
    public IReadOnlyList<SharedLocationConflict> Conflicts => SharingConflicts ?? [];
    public IReadOnlyList<string> ValidationErrors => Errors ?? [];
    public bool HasNegativeBalance => ResultingBalances.Any(balance => balance.IsNegative) || Plates?.Any(x => x.Quantity < 0) == true;
}
