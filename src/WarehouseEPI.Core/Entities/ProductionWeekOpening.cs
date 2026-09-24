namespace WarehouseEPI.Core.Entities;

// An admission of existing work, never a second schedule line or inventory receipt.
public sealed class ProductionWeekOpening
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WeekId { get; set; }
    public Guid SourceWeekId { get; set; }
    public Guid SourceLineId { get; set; }
    public Guid ProductId { get; set; }
    public ProductionDailyArea Area { get; set; }
    public decimal Quantity { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public uint Version { get; set; }
}
