namespace WarehouseEPI.Core.Entities;

public sealed class ProductLot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public required string Number { get; set; }
    public required string NormalizedNumber { get; set; }
    public DateOnly? ExpirationDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Product Product { get; set; } = null!;
}
