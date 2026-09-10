namespace WarehouseEPI.Core.Entities;

public enum ProductionWorkOrderStatus { Draft, Released, InProgress, Paused, Closed, Cancelled }
public enum ProductionEventType
{
    Created, Released, QuantityAuthorized, Processed, Reworked, Delivered, Received,
    DifferenceReturned, DifferenceLost, WarehouseReceived, Paused, Resumed, Closed, Cancelled
}

public sealed class ProductionStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ProductionShift
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ProductionRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Product Product { get; set; } = null!;
    public ICollection<ProductionRouteStage> Stages { get; set; } = [];
}

public sealed class ProductionRouteStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RouteId { get; set; }
    public Guid StageId { get; set; }
    public int Sequence { get; set; }
    public ProductionRoute Route { get; set; } = null!;
    public ProductionStage Stage { get; set; } = null!;
}

public sealed class ProductionWorkOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CreateOperationId { get; set; }
    public required string CreateFingerprint { get; set; }
    public required string Number { get; set; }
    public string? ExternalReference { get; set; }
    public Guid ProductId { get; set; }
    public short UnitId { get; set; }
    public decimal TargetQuantity { get; set; }
    public decimal AuthorizedQuantity { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? Notes { get; set; }
    public ProductionWorkOrderStatus Status { get; set; } = ProductionWorkOrderStatus.Draft;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public uint Version { get; set; }
    public Product Product { get; set; } = null!;
    public Unit Unit { get; set; } = null!;
    public User CreatedByUser { get; set; } = null!;
    public ICollection<ProductionWorkOrderStage> Stages { get; set; } = [];
    public ICollection<ProductionEvent> Events { get; set; } = [];
}

public sealed class ProductionWorkOrderStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkOrderId { get; set; }
    public Guid SourceStageId { get; set; }
    public int Sequence { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public ProductionStage SourceStage { get; set; } = null!;
}

public sealed class ProductionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid? WorkOrderStageId { get; set; }
    public Guid? RelatedStageId { get; set; }
    public ProductionEventType Type { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public Guid? ShiftId { get; set; }
    public decimal Quantity { get; set; }
    public decimal GoodQuantity { get; set; }
    public decimal ReworkQuantity { get; set; }
    public decimal ScrapQuantity { get; set; }
    public Guid? InventoryMovementId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public ProductionWorkOrderStage? WorkOrderStage { get; set; }
    public ProductionWorkOrderStage? RelatedStage { get; set; }
    public User ResponsibleUser { get; set; } = null!;
    public ProductionShift? Shift { get; set; }
    public InventoryMovement? InventoryMovement { get; set; }
}
