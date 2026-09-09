namespace WarehouseEPI.Core.Entities;

public sealed class CycleCountPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid LocationId { get; set; }
    public CycleCountFrequency Frequency { get; set; } = CycleCountFrequency.Monthly;
    public DateOnly AnchorDate { get; set; }
    public DateOnly NextDueDate { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Product Product { get; set; } = null!;
    public Location Location { get; set; } = null!;
    public User? CreatedByUser { get; set; }
    public User? UpdatedByUser { get; set; }
    public ICollection<CycleCountPlannedProduct> Dispatches { get; set; } = [];
    public ICollection<CycleCountPlanEvent> Events { get; set; } = [];
}
