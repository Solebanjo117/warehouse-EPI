namespace WarehouseEPI.Core.Entities;

public sealed class InventoryBalance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? LotId { get; set; }
    public decimal Quantity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public uint Version { get; set; }

    public Product Product { get; set; } = null!;
    public Location Location { get; set; } = null!;
    public ProductLot? Lot { get; set; }
}
