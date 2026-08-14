namespace WarehouseEPI.Core.Entities;

public sealed class InventoryBalanceChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MovementLineId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? LotId { get; set; }
    public string? LotNumberSnapshot { get; set; }
    public DateOnly? LotDateSnapshot { get; set; }
    public decimal DeltaQuantity { get; set; }
    public decimal PreviousQuantity { get; set; }
    public decimal ResultingQuantity { get; set; }

    public InventoryMovementLine MovementLine { get; set; } = null!;
    public Location Location { get; set; } = null!;
    public ProductLot? Lot { get; set; }
}
