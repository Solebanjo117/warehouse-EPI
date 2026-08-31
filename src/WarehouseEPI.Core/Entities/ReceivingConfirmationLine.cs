namespace WarehouseEPI.Core.Entities;

public sealed class ReceivingConfirmationLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReceivingConfirmationId { get; set; }
    public Guid? ReceivingDocumentLineId { get; set; }
    public Guid InventoryMovementLineId { get; set; }
    public string? ExternalLotReference { get; set; }

    public ReceivingConfirmation ReceivingConfirmation { get; set; } = null!;
    public ReceivingDocumentLine? ReceivingDocumentLine { get; set; }
    public InventoryMovementLine InventoryMovementLine { get; set; } = null!;
}
