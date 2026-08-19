namespace WarehouseEPI.Core.Entities;

public enum WarehouseMapElementKind { Rack, Area }

public sealed class WarehouseMapElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public short LayoutId { get; set; } = 1;
    public WarehouseMapElementKind Kind { get; set; }
    public string? RowCode { get; set; }
    public short? RackNumber { get; set; }
    public Guid? LocationId { get; set; }
    public decimal X { get; set; }
    public decimal Y { get; set; }
    public decimal Width { get; set; }
    public decimal Height { get; set; }
    public short Rotation { get; set; }
    public int ZIndex { get; set; }
    public bool IsVisible { get; set; } = true;
    public WarehouseMapLayout Layout { get; set; } = null!;
    public Location? Location { get; set; }
}
