namespace WarehouseEPI.Core.Entities;

public enum OperationalExceptionCategory
{
    NegativeInventory,
    BelowMinimum,
    UnassignedBalance,
    RestrictedInventory,
    StagnantInventory,
    CycleCountStale,
    CycleCountPending,
    AgedWip
}
