namespace WarehouseEPI.Core.Entities;

public sealed class ProductionBalanceEdit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid WeekId { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string? Reason { get; set; }
    public ICollection<ProductionBalanceEditItem> Items { get; set; } = [];
}

public sealed class ProductionBalanceEditItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EditId { get; set; }
    public Guid ProductId { get; set; }
    public ProductionDailyArea Area { get; set; }
    public Guid ShiftId { get; set; }
    public decimal PreviousTotal { get; set; }
    public decimal RequestedTotal { get; set; }
    public Guid? ReversedCaptureId { get; set; }
    public Guid? CreatedCaptureId { get; set; }
}
