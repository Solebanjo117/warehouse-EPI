namespace WarehouseEPI.Core.Entities;

public enum WarehouseMapLayerCode
{
    Structure,
    Aisles,
    Zones,
    Text,
    Dimensions,
    Operations
}

public sealed class WarehouseMapLayer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public short LayoutId { get; set; } = 1;
    public WarehouseMapLayerCode Code { get; set; }
    public required string Name { get; set; }
    public short SortOrder { get; set; }
    public bool IsLocked { get; set; } = true;
    public WarehouseMapLayout Layout { get; set; } = null!;
    public ICollection<WarehouseMapArchitecturalElement> ArchitecturalElements { get; set; } = [];
}
