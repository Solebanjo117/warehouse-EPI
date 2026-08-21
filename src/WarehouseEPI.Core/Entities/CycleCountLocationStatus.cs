namespace WarehouseEPI.Core.Entities;

public enum CycleCountLocationStatus
{
    Pending,
    Counting,
    UnderReview,
    RecountRequested,
    Stale,
    Completed,
    Cancelled
}
