namespace WarehouseEPI.Core.Entities;

public sealed class ProductLocationAssignment
{
    public Guid ProductId { get; set; }
    public Guid LocationId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Product Product { get; set; } = null!;
    public Location Location { get; set; } = null!;
}
