namespace WarehouseEPI.Core.Entities;

public sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Sku { get; set; }
    public string? Description { get; set; }
    public string? ExternalReference { get; set; }
    public short? ProductTypeId { get; set; }
    public short? ProductClassId { get; set; }
    public short BaseUnitId { get; set; }
    public decimal MinimumStock { get; set; }
    public bool TracksLots { get; set; }
    public bool TracksExpiration { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Unit BaseUnit { get; set; } = null!;
    public ProductType? ProductType { get; set; }
    public ProductClass? ProductClass { get; set; }
    public ICollection<ProductBarcode> Barcodes { get; set; } = [];
    public ICollection<ProductLocationAssignment> LocationAssignments { get; set; } = [];
    public ICollection<ProductLot> Lots { get; set; } = [];
}
