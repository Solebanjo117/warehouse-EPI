namespace WarehouseEPI.Core.Entities;

public sealed class ReceivingDocumentEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OperationId { get; set; }
    public string? RequestFingerprint { get; set; }
    public Guid ReceivingDocumentId { get; set; }
    public ReceivingDocumentEventType Type { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    public ReceivingDocument ReceivingDocument { get; set; } = null!;
    public User? ActorUser { get; set; }
}
