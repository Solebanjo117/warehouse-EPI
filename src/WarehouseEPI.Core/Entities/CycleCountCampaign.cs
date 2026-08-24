namespace WarehouseEPI.Core.Entities;

public sealed class CycleCountCampaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public long Number { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public CycleCountCampaignStatus Status { get; set; } = CycleCountCampaignStatus.Draft;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? LastActionByUserId { get; set; }

    public User CreatedByUser { get; set; } = null!;
    public User? LastActionByUser { get; set; }
    public ICollection<CycleCountLocation> Locations { get; set; } = [];
}
