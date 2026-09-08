namespace WarehouseEPI.Core.Entities;

/// <summary>Producto explícitamente incluido por un plan al liberar una ubicación de conteo.</summary>
public sealed class CycleCountPlannedProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CycleCountLocationId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? CycleCountPlanId { get; set; }
    public DateOnly? ScheduledFor { get; set; }

    public CycleCountLocation CycleCountLocation { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public CycleCountPlan? CycleCountPlan { get; set; }
}
