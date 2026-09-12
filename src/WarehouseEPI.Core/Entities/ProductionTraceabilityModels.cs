namespace WarehouseEPI.Core.Entities;

public sealed class ProductionRecipe
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public int Version { get; set; }
    public decimal BaseQuantity { get; set; }
    public bool IsActive { get; set; } = true;
    public required string Reason { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Product Product { get; set; } = null!;
    public User CreatedByUser { get; set; } = null!;
    public ICollection<ProductionRecipeLine> Lines { get; set; } = [];
}

public sealed class ProductionRecipeLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecipeId { get; set; }
    public Guid MaterialProductId { get; set; }
    public Guid StageId { get; set; }
    public decimal Quantity { get; set; }
    public ProductionRecipe Recipe { get; set; } = null!;
    public Product MaterialProduct { get; set; } = null!;
    public ProductionStage Stage { get; set; } = null!;
}

public sealed class ProductionOrderMaterialPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkOrderId { get; set; }
    public Guid WorkOrderStageId { get; set; }
    public Guid MaterialProductId { get; set; }
    public short UnitId { get; set; }
    public decimal PlannedQuantity { get; set; }
    public decimal OriginalPlannedQuantity { get; set; }
    public string? AdjustmentReason { get; set; }
    public Guid? AdjustedByUserId { get; set; }
    public DateTimeOffset? AdjustedAt { get; set; }
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public ProductionWorkOrderStage WorkOrderStage { get; set; } = null!;
    public Product MaterialProduct { get; set; } = null!;
    public Unit Unit { get; set; } = null!;
    public User? AdjustedByUser { get; set; }
}

public sealed class ProductionBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CreateOperationId { get; set; }
    public required string CreateFingerprint { get; set; }
    public Guid WorkOrderId { get; set; }
    public required string Number { get; set; }
    public decimal AssignedQuantity { get; set; }
    public Guid FinishedProductLotId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public uint Version { get; set; }
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public ProductLot FinishedProductLot { get; set; } = null!;
    public User CreatedByUser { get; set; } = null!;
    public ICollection<ProductionBatchResult> Results { get; set; } = [];
}

public sealed class ProductionBatchResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid BatchId { get; set; }
    public Guid WorkOrderStageId { get; set; }
    public Guid ShiftId { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public bool IsRework { get; set; }
    public decimal InputQuantity { get; set; }
    public decimal GoodQuantity { get; set; }
    public decimal ReworkQuantity { get; set; }
    public decimal ScrapQuantity { get; set; }
    public string? DifferenceReason { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProductionBatch Batch { get; set; } = null!;
    public ProductionWorkOrderStage WorkOrderStage { get; set; } = null!;
    public ProductionShift Shift { get; set; } = null!;
    public User ResponsibleUser { get; set; } = null!;
    public ICollection<ProductionBatchMaterialConsumption> Materials { get; set; } = [];
}

public sealed class ProductionBatchMaterialConsumption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchResultId { get; set; }
    public Guid IssueLinkId { get; set; }
    public Guid MaterialLotId { get; set; }
    public decimal Quantity { get; set; }
    public ProductionBatchResult BatchResult { get; set; } = null!;
    public ProductionMaterialIssueLink IssueLink { get; set; } = null!;
    public ProductLot MaterialLot { get; set; } = null!;
}
