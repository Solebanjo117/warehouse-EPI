namespace WarehouseEPI.Core.Entities;

public sealed class OperationalExceptionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OperationId { get; set; }
    public Guid OperationalExceptionCaseId { get; set; }
    public OperationalExceptionEventType Type { get; set; }
    public OperationalExceptionStatus? PreviousStatus { get; set; }
    public OperationalExceptionStatus? CurrentStatus { get; set; }
    public Guid? PreviousAssignedUserId { get; set; }
    public Guid? CurrentAssignedUserId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    public OperationalExceptionCase OperationalExceptionCase { get; set; } = null!;
    public User? PreviousAssignedUser { get; set; }
    public User? CurrentAssignedUser { get; set; }
    public User? ActorUser { get; set; }
}
