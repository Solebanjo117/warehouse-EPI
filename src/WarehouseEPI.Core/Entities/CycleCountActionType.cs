namespace WarehouseEPI.Core.Entities;

public enum CycleCountActionType
{
    Created,
    Released,
    AttemptStarted,
    AttemptSubmitted,
    RecountRequested,
    StaleDetected,
    AdjustmentApproved,
    LocationCompleted,
    CampaignCompleted,
    Cancelled
}
