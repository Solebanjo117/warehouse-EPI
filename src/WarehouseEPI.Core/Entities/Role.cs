namespace WarehouseEPI.Core.Entities;

public sealed class Role
{
    public short Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<User> Users { get; set; } = [];
}
