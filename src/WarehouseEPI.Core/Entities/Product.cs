namespace WarehouseEPI.Core.Entities;

public sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? TypeCode { get; set; }
    public string? ClassCode { get; set; }
    public short BaseUnitId { get; set; }
    public decimal MinimumStock { get; set; }
    public bool TracksLots { get; set; }
    public bool TracksExpiration { get; set; }
    public bool AllowsNegativeStock { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Unit BaseUnit { get; set; } = null!;
    public ICollection<ProductBarcode> Barcodes { get; set; } = [];
}
