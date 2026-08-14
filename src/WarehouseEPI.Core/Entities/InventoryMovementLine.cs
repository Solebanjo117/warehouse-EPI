namespace WarehouseEPI.Core.Entities;

public sealed class InventoryMovementLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MovementId { get; set; }
    public int LineNumber { get; set; }
    public Guid ProductId { get; set; }
    public short UnitId { get; set; }
    public decimal Quantity { get; set; }
    public Guid? SourceLocationId { get; set; }
    public Guid? DestinationLocationId { get; set; }
    public Guid? LotId { get; set; }
    public decimal? PreviousQuantity { get; set; }
    public decimal? AdjustmentDelta { get; set; }

    public InventoryMovement Movement { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public Unit Unit { get; set; } = null!;
    public Location? SourceLocation { get; set; }
    public Location? DestinationLocation { get; set; }
    public ProductLot? Lot { get; set; }
    public ICollection<InventoryBalanceChange> BalanceChanges { get; set; } = [];
}
