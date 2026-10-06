namespace WarehouseEPI.Core.Entities;

/// <summary>Material delivered to production. This is a document, never warehouse stock.</summary>
public sealed class WipDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? MovementLineId { get; set; }
    public Guid? CutoverId { get; set; }
    public Guid ProductId { get; set; }
    public Guid WipLocationId { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public decimal Quantity { get; set; }
    public bool IsOpening { get; set; }
    public bool IsCancelled { get; set; }
    public Product Product { get; set; } = null!;
    public Location WipLocation { get; set; } = null!;
    public User ResponsibleUser { get; set; } = null!;
    public InventoryMovementLine? MovementLine { get; set; }
    public ICollection<WipDocumentLot> Lots { get; set; } = [];
    public ICollection<WipDocumentApplication> Applications { get; set; } = [];
    public ICollection<WipDocumentAssignment> Assignments { get; set; } = [];
}

public sealed class WipDocumentLot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid? LotId { get; set; }
    public decimal Quantity { get; set; }
    public WipDocument Document { get; set; } = null!;
    public ProductLot? Lot { get; set; }
}

public sealed class WipDocumentAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid IssueLinkId { get; set; }
    public decimal Quantity { get; set; }
    public WipDocument Document { get; set; } = null!;
    public ProductionMaterialIssueLink IssueLink { get; set; } = null!;
}

public enum WipDocumentApplicationKind { Consumption, Scrap, WarehouseReturn, SupplierReturn, Reversal }

/// <summary>Immutable application, with exact documentary lot allocations and optional warehouse entry.</summary>
public sealed class WipDocumentApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public string Fingerprint { get; set; } = "";
    public Guid DocumentId { get; set; }
    public Guid? IssueLinkId { get; set; }
    public Guid? LotId { get; set; }
    public Guid? InventoryMovementLineId { get; set; }
    public Guid? ReversesApplicationId { get; set; }
    public WipDocumentApplicationKind Kind { get; set; }
    public decimal Quantity { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public WipDocument Document { get; set; } = null!;
    public InventoryMovementLine? InventoryMovementLine { get; set; }
}

public sealed class WipDocumentCutover
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Singleton { get; set; } = 1;
    public Guid OperationId { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Revision { get; set; } = "";
    public string Reason { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
}
