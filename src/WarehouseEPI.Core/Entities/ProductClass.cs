namespace WarehouseEPI.Core.Entities;

public sealed class ProductClass
{
    public short Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Product> Products { get; set; } = [];
}
