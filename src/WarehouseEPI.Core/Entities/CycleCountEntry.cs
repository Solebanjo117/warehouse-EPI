namespace WarehouseEPI.Core.Entities;

public sealed class CycleCountEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleCountAttemptId { get; set; }
    public Guid ProductId { get; set; }
    public short UnitId { get; set; }
    public decimal ExpectedQuantity { get; set; }
    public uint ExpectedBalanceVersion { get; set; }
    public decimal? CountedQuantity { get; set; }
    public bool IsUnexpectedProduct { get; set; }

    public CycleCountAttempt CycleCountAttempt { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public Unit Unit { get; set; } = null!;
}
