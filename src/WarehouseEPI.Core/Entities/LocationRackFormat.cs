namespace WarehouseEPI.Core.Entities;

public sealed class LocationRackFormat
{
    public required string RowCode { get; set; }
    public short RackNumber { get; set; }
    public int Columns { get; set; } = 3;
    public int Levels { get; set; } = 3;
}
