namespace WarehouseEPI.Core.Entities;

public sealed class WarehouseMapLayout
{
    public short Id { get; set; } = 1;
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
    public uint RowVersion { get; set; }
    public User? UpdatedByUser { get; set; }
    public ICollection<WarehouseMapElement> Elements { get; set; } = [];
    public ICollection<WarehouseMapLayer> Layers { get; set; } = [];
    public ICollection<WarehouseMapArchitecturalElement> ArchitecturalElements { get; set; } = [];
}
