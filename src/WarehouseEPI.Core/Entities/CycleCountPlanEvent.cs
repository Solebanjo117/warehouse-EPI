namespace WarehouseEPI.Core.Entities;

public sealed class CycleCountPlanEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleCountPlanId { get; set; }
    public CycleCountPlanEventType Type { get; set; }
    public Guid? ResponsibleUserId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? CycleCountLocationId { get; set; }
    public CycleCountFrequency? PreviousFrequency { get; set; }
    public CycleCountFrequency? NewFrequency { get; set; }
    public DateOnly? PreviousAnchorDate { get; set; }
    public DateOnly? NewAnchorDate { get; set; }
    public DateOnly? PreviousNextDueDate { get; set; }
    public DateOnly? NewNextDueDate { get; set; }
    public bool? PreviousIsActive { get; set; }
    public bool? NewIsActive { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    public CycleCountPlan CycleCountPlan { get; set; } = null!;
    public User? ResponsibleUser { get; set; }
    public CycleCountCampaign? Campaign { get; set; }
    public CycleCountLocation? CycleCountLocation { get; set; }
}
