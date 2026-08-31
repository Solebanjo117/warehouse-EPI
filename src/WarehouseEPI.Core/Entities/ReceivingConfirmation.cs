namespace WarehouseEPI.Core.Entities;

public sealed class ReceivingConfirmation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid ReceivingDocumentId { get; set; }
    public Guid InventoryMovementId { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public bool DifferenceAcknowledged { get; set; }
    public string? DifferenceNotes { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    public ReceivingDocument ReceivingDocument { get; set; } = null!;
    public InventoryMovement InventoryMovement { get; set; } = null!;
    public User ResponsibleUser { get; set; } = null!;
    public ICollection<ReceivingConfirmationLine> Lines { get; set; } = [];
}
