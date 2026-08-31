namespace WarehouseEPI.Core.Entities;

public sealed class OperationalExceptionCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public OperationalExceptionCategory Category { get; set; }
    public OperationalExceptionSeverity Severity { get; set; }
    public required string ConditionKey { get; set; }
    public OperationalExceptionStatus Status { get; set; } = OperationalExceptionStatus.New;
    public Guid? ProductId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? CycleCountLocationId { get; set; }
    public required string PrimaryText { get; set; }
    public required string SecondaryText { get; set; }
    public string? ValueText { get; set; }
    public required string TargetUrl { get; set; }
    public Guid? AssignedUserId { get; set; }
    public DateTimeOffset FirstDetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastDetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public uint Version { get; set; }

    public Product? Product { get; set; }
    public Location? Location { get; set; }
    public CycleCountLocation? CycleCountLocation { get; set; }
    public User? AssignedUser { get; set; }
    public ICollection<OperationalExceptionEvent> Events { get; set; } = [];
}
