namespace WarehouseEPI.Core.Entities;

public sealed class CycleCountLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid LocationId { get; set; }
    public int SortOrder { get; set; }
    public CycleCountLocationStatus Status { get; set; } = CycleCountLocationStatus.Pending;
    public Guid? AdjustmentMovementId { get; set; }
    public CycleCountAdjustmentReason? AdjustmentReason { get; set; }
    public string? AdjustmentReasonNotes { get; set; }
    public Guid? LastActionByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public CycleCountCampaign Campaign { get; set; } = null!;
    public Location Location { get; set; } = null!;
    public InventoryMovement? AdjustmentMovement { get; set; }
    public User? LastActionByUser { get; set; }
    public ICollection<CycleCountAttempt> Attempts { get; set; } = [];
}
