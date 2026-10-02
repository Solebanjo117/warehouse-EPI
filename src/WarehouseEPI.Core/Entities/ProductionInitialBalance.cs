namespace WarehouseEPI.Core.Entities;

// The definitive opening total, independent of physical allocation provenance.
public sealed class ProductionInitialBalance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WeekId { get; set; }
    public Guid ProductId { get; set; }
    public ProductionDailyArea Area { get; set; }
    public decimal Quantity { get; set; }
    public uint Version { get; set; }
}
