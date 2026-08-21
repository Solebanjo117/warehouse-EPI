namespace WarehouseEPI.Core.Entities;

public sealed class CycleCountAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public Guid? SubmissionOperationId { get; set; }
    public Guid CycleCountLocationId { get; set; }
    public int AttemptNumber { get; set; }
    public CycleCountAttemptStatus Status { get; set; } = CycleCountAttemptStatus.Counting;
    public Guid StartedByUserId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? SubmittedByUserId { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }

    public CycleCountLocation CycleCountLocation { get; set; } = null!;
    public User StartedByUser { get; set; } = null!;
    public User? SubmittedByUser { get; set; }
    public ICollection<CycleCountEntry> Entries { get; set; } = [];
}
