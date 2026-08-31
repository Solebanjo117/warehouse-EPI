namespace WarehouseEPI.Core.Entities;

public sealed class ReceivingDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public ReceivingDocumentType Type { get; set; }
    public required string Number { get; set; }
    public required string NormalizedNumber { get; set; }
    public required string Origin { get; set; }
    public required string NormalizedOrigin { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public ReceivingDocumentStatus Status { get; set; } = ReceivingDocumentStatus.Open;
    public string? Notes { get; set; }
    public Guid OpenedByUserId { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? CloseReason { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public uint Version { get; set; }

    public User OpenedByUser { get; set; } = null!;
    public User? ClosedByUser { get; set; }
    public User? CancelledByUser { get; set; }
    public ICollection<ReceivingDocumentLine> Lines { get; set; } = [];
    public ICollection<ReceivingConfirmation> Confirmations { get; set; } = [];
    public ICollection<ReceivingDocumentEvent> Events { get; set; } = [];
}
