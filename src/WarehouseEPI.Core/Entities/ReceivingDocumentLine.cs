namespace WarehouseEPI.Core.Entities;

public sealed class ReceivingDocumentLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReceivingDocumentId { get; set; }
    public int LineNumber { get; set; }
    public Guid ProductId { get; set; }
    public short UnitId { get; set; }
    public decimal ExpectedQuantity { get; set; }

    public ReceivingDocument ReceivingDocument { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public Unit Unit { get; set; } = null!;
    public ICollection<ReceivingConfirmationLine> ConfirmationLines { get; set; } = [];
}
