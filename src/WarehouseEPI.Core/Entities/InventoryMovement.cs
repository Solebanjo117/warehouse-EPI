namespace WarehouseEPI.Core.Entities;

public sealed class InventoryMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public InventoryMovementType Type { get; set; }
    public InventoryMovementPurpose Purpose { get; set; }
    public Guid? OperationalAreaId { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    public User ResponsibleUser { get; set; } = null!;
    public Location? OperationalArea { get; set; }
    public ICollection<InventoryMovementLine> Lines { get; set; } = [];
}
