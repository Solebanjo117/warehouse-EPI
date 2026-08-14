namespace WarehouseEPI.Core.Entities;

public sealed class Location
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public LocationKind Kind { get; set; }
    public string? RowCode { get; set; }
    public short? RackNumber { get; set; }
    public short? PalletNumber { get; set; }
    public string? Description { get; set; }
    public bool IsBlocked { get; set; }
    public string? BlockReason { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ProductLocationAssignment> ProductAssignments { get; set; } = [];

    public bool IsOperational => IsActive && !IsBlocked;
    public short? LevelNumber => PalletNumber is null ? null : (short)((PalletNumber.Value - 1) / 3 + 1);
    public short? HorizontalPosition => PalletNumber is null ? null : (short)((PalletNumber.Value - 1) % 3 + 1);
}
