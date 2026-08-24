namespace WarehouseEPI.Core.Entities;

public enum WarehouseMapArchitecturalElementKind
{
    Rectangle,
    Polyline,
    Text
}

public sealed class WarehouseMapArchitecturalElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public short LayoutId { get; set; } = 1;
    public Guid LayerId { get; set; }
    public WarehouseMapArchitecturalElementKind Kind { get; set; }
    public string? Label { get; set; }
    public required string GeometryJson { get; set; }
    public required string StrokeToken { get; set; }
    public required string FillToken { get; set; }
    public decimal StrokeWidth { get; set; }
    public bool IsDashed { get; set; }
    public int ZIndex { get; set; }
    public bool IsLocked { get; set; }
    public WarehouseMapLayout Layout { get; set; } = null!;
    public WarehouseMapLayer Layer { get; set; } = null!;
}
