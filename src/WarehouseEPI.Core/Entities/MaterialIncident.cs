namespace WarehouseEPI.Core.Entities;

public enum MaterialIncidentStatus { Open, Reviewing, Resolved, Voided }
public enum MaterialIncidentScope { Undetermined, Receiving, Warehouse }
public enum MaterialIncidentKind { Damage, QuantityDifference, WrongProduct, Unidentified, Other }
public enum MaterialIncidentDifference { Undetermined, Shortage, Surplus }

public sealed class MaterialIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Folio => $"INC-{Id:N}".ToUpperInvariant();
    public Guid ProductId { get; set; }
    public short UnitId { get; set; }
    public Guid DetectionLocationId { get; set; }
    public Guid? PlateId { get; set; }
    public Guid? ArrivalLineId { get; set; }
    public MaterialIncidentScope Scope { get; set; }
    public MaterialIncidentKind Kind { get; set; }
    public MaterialIncidentDifference Difference { get; set; }
    public decimal? Quantity { get; set; }
    public string Description { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public MaterialIncidentStatus Status { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset ReportedAt { get; set; }
    public Guid ReportedById { get; set; }
    public Product Product { get; set; } = null!;
    public Location DetectionLocation { get; set; } = null!;
    public Unit Unit { get; set; } = null!;
    public User ReportedBy { get; set; } = null!;
}

public sealed class MaterialIncidentEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Guid OperationId { get; set; }
    public string Fingerprint { get; set; } = "";
    public long Version { get; set; }
    public string Action { get; set; } = "";
    public string Comment { get; set; } = "";
    public Guid? CorrectionId { get; set; }
    public MaterialIncidentStatus Status { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid ResponsibleId { get; set; }
    public User Responsible { get; set; } = null!;
}

public sealed class MaterialIncidentPhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Guid EventId { get; set; }
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public byte[] Content { get; set; } = [];
}
