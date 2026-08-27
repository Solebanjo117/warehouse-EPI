namespace WarehouseEPI.Core.Entities;

public sealed class BusinessSettings
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;
    public required string BusinessName { get; set; }
    public required string WarehouseName { get; set; }
    public required string WarehouseCode { get; set; }
    public required string TimeZoneId { get; set; }
    public int WipReminderDays { get; set; } = 7;
    public string? LogoFileName { get; set; }
    public string? LogoContentType { get; set; }
    public string? LogoHash { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid UpdatedByUserId { get; set; }

    public User UpdatedByUser { get; set; } = null!;
}
