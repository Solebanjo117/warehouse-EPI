namespace WarehouseEPI.Core.Entities;

public sealed class CycleCountAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OperationId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid? CycleCountLocationId { get; set; }
    public Guid? CycleCountAttemptId { get; set; }
    public Guid? ReviewBatchId { get; set; }
    public CycleCountActionType Type { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    public CycleCountCampaign Campaign { get; set; } = null!;
    public CycleCountLocation? CycleCountLocation { get; set; }
    public CycleCountAttempt? CycleCountAttempt { get; set; }
    public CycleCountReviewBatch? ReviewBatch { get; set; }
    public User ResponsibleUser { get; set; } = null!;
}
