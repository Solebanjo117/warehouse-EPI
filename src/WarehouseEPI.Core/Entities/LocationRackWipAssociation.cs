namespace WarehouseEPI.Core.Entities;

/// <summary>Current documentary WIP shared by the WIP positions of a rack.</summary>
public sealed class LocationRackWipAssociation
{
    public required string RowCode { get; set; }
    public short RackNumber { get; set; }
    public Guid WipAreaId { get; set; }
    public Location WipArea { get; set; } = null!;
}
