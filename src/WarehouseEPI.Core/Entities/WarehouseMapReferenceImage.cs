namespace WarehouseEPI.Core.Entities;

public sealed class WarehouseMapReferenceImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public short LayoutId { get; set; } = 1;
    public required string OriginalFileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string ContentType { get; set; }
    public required string Sha256 { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public decimal X { get; set; }
    public decimal Y { get; set; }
    public decimal Width { get; set; }
    public decimal Height { get; set; }
    public short Rotation { get; set; }
    public decimal Opacity { get; set; } = 0.35m;
    public bool IsLocked { get; set; } = true;
    public bool IsArchived { get; set; }
    public decimal? CalibrationAX { get; set; }
    public decimal? CalibrationAY { get; set; }
    public decimal? CalibrationBX { get; set; }
    public decimal? CalibrationBY { get; set; }
    public decimal? CalibrationDistanceInches { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public WarehouseMapLayout Layout { get; set; } = null!;
    public User CreatedByUser { get; set; } = null!;
}
