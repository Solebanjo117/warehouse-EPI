namespace WarehouseEPI.Core.Entities;

public enum ProductionReasonCategory { Scrap, Rework, Difference, Adjustment }

public sealed class ProductionReason
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ProductionReasonCategory Category { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool RequiresComment { get; set; }
    public uint Version { get; set; }
}

// Append-only snapshots also cover configuration changes; no PIN is persisted.
public sealed class ProductionExecutionAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public string Fingerprint { get; set; } = "";
    public Guid? WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }
    public string Action { get; set; } = "";
    public Guid ResponsibleUserId { get; set; }
    public Guid? AuthorizedByUserId { get; set; }
    public string Reason { get; set; } = "";
    public string BeforeJson { get; set; } = "{}";
    public string AfterJson { get; set; } = "{}";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ProductionReworkCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkOrderId { get; set; }
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public Guid BatchId { get; set; }
    public ProductionBatch Batch { get; set; } = null!;
    public Guid WorkOrderStageId { get; set; }
    public ProductionWorkOrderStage WorkOrderStage { get; set; } = null!;
    public Guid OriginResultId { get; set; }
    public ProductionBatchResult OriginResult { get; set; } = null!;
    public decimal InitialQuantity { get; set; }
    public DateTimeOffset OriginAt { get; set; }
    public ICollection<ProductionReworkAttempt> Attempts { get; set; } = [];
}

public sealed class ProductionReworkAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReworkCaseId { get; set; }
    public ProductionReworkCase ReworkCase { get; set; } = null!;
    public Guid ResultId { get; set; }
    public ProductionBatchResult Result { get; set; } = null!;
}

// A retained reservation belongs to one case; actual quantities remain in the inventory engine.
public sealed class ProductionReworkRetention
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReworkCaseId { get; set; }
    public ProductionReworkCase ReworkCase { get; set; } = null!;
    public Guid? IssueLinkId { get; set; }
    public ProductionMaterialIssueLink? IssueLink { get; set; }
    public Guid? WarehouseReservationId { get; set; }
    public ProductionWarehouseReservation? WarehouseReservation { get; set; }
    public decimal Quantity { get; set; }
}
