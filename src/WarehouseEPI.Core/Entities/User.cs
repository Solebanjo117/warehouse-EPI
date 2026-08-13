namespace WarehouseEPI.Core.Entities;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FullName { get; set; }
    public short RoleId { get; set; }

    // PinLookup localiza al usuario mediante un HMAC; PinHash verifica el NIP.
    // Ninguno de los dos campos contiene el NIP en texto plano.
    public required string PinLookup { get; set; }
    public required string PinHash { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Role Role { get; set; } = null!;
}
