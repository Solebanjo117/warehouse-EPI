namespace WarehouseEPI.Core.Entities;

/// <summary>Immutable relationship between a confirmed movement and its corrective movements.</summary>
public sealed class InventoryMovementCorrection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public InventoryMovementCorrectionType Type { get; set; }
    public Guid OriginalMovementId { get; set; }
    public Guid ReversalMovementId { get; set; }
    public Guid? ReplacementMovementId { get; set; }
    public required string Reason { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid AuthorizedByUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    public InventoryMovement OriginalMovement { get; set; } = null!;
    public InventoryMovement ReversalMovement { get; set; } = null!;
    public InventoryMovement? ReplacementMovement { get; set; }
    public User RequestedByUser { get; set; } = null!;
    public User AuthorizedByUser { get; set; } = null!;
}
