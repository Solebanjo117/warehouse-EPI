namespace WarehouseEPI.Core.Entities;

public sealed class Location
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }

    // Estos componentes permanecen opcionales hasta validar el layout fisico.
    // El codigo es la identidad operativa y admite valores existentes como 1A1.
    public string? Aisle { get; set; }
    public short? Shelf { get; set; }
    public short? LevelNumber { get; set; }
    public string? PalletPosition { get; set; }
    public string? Description { get; set; }
    public bool IsBlocked { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
