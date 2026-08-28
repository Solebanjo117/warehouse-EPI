namespace WarehouseEPI.Core.Entities;

public sealed class CycleCountReviewBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid CampaignId { get; set; }
    public Guid AuthorizedByUserId { get; set; }
    public DateTimeOffset AuthorizedAt { get; set; } = DateTimeOffset.UtcNow;

    public CycleCountCampaign Campaign { get; set; } = null!;
    public User AuthorizedByUser { get; set; } = null!;
    public ICollection<CycleCountAction> Actions { get; set; } = [];
}
