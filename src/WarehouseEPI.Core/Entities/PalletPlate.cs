namespace WarehouseEPI.Core.Entities;

/// <summary>A physical, single-product pallet. Quantities are a subdivision of inventory, never extra stock.</summary>
public sealed class PalletPlate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid LocationId { get; set; }
    public Guid? OriginMovementId { get; set; }
    public decimal Quantity { get; set; }
    public long Version { get; set; }
    public bool IsVoided { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Product Product { get; set; } = null!;
    public Location Location { get; set; } = null!;
    public List<PalletPlateLot> Lots { get; set; } = [];
    public string Identifier => $"PLT-{Id:N}".ToUpperInvariant();
    public string Status => IsVoided ? "Anulada" : Quantity < 0 ? "Con diferencia" : Quantity == 0 ? "Agotada" : "Disponible";
}

public sealed class PalletPlateLot
{
    public Guid PlateId { get; set; }
    public Guid LotId { get; set; }
    public decimal Quantity { get; set; }
    public PalletPlate Plate { get; set; } = null!;
    public ProductLot Lot { get; set; } = null!;
}

/// <summary>Immutable before/after record, including the lot composition used for exact reversals.</summary>
public sealed class PalletPlateEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlateId { get; set; }
    public Guid OperationId { get; set; }
    public Guid? MovementId { get; set; }
    public Guid? MovementLineId { get; set; }
    public Guid? ResponsibleUserId { get; set; }
    public Guid? ReversesEventId { get; set; }
    public long PlateVersion { get; set; }
    public string Kind { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
    public PalletPlate Plate { get; set; } = null!;
}
