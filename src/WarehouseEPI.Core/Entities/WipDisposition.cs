namespace WarehouseEPI.Core.Entities;

public sealed class WipDisposition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid OriginalMovementLineId { get; set; }
    public WipDispositionType Type { get; set; }
    public decimal Quantity { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public Guid? DestinationLocationId { get; set; }
    public Guid? InventoryMovementId { get; set; }
    public Guid? ReversesDispositionId { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    public InventoryMovementLine OriginalMovementLine { get; set; } = null!;
    public User ResponsibleUser { get; set; } = null!;
    public Location? DestinationLocation { get; set; }
    public InventoryMovement? InventoryMovement { get; set; }
    public WipDisposition? ReversesDisposition { get; set; }
}
