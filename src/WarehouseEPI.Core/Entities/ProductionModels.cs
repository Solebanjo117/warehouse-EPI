namespace WarehouseEPI.Core.Entities;

public enum ProductionWorkOrderStatus { Draft, Released, InProgress, Paused, Closed, Cancelled, PrincipalClosed }
public enum ProductionEventType
{
    Created, Released, QuantityAuthorized, Processed, Reworked, Delivered, Received,
    DifferenceReturned, DifferenceLost, WarehouseReceived, Paused, Resumed, Closed, Cancelled, ResultReversed, MaterialPlanAdjusted
}
public enum ProductionMaterialOperationType { Consumption, WarehouseReturn, SupplierReturn, Reversal, Scrap }
public enum ProductionMaterialReturnEffect { Replenish, Surplus }

public sealed class ProductionStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? DefaultWipLocationId { get; set; }
    public string? DefaultWipRowCode { get; set; }
    public short? DefaultWipRackNumber { get; set; }
    public int? InactivityAlertHours { get; set; }
    public int? ReworkAlertHours { get; set; }
    public Location? DefaultWipLocation { get; set; }
    public ICollection<ProductionProcessWipTarget> WipTargets { get; set; } = [];
}

public sealed class ProductionMaterialWipDefault
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid ProductionStageId { get; set; }
    public Guid? LocationId { get; set; }
    public string? RowCode { get; set; }
    public short? RackNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Product Product { get; set; } = null!;
    public ProductionStage ProductionStage { get; set; } = null!;
    public Location? Location { get; set; }
}

public sealed class ProductionMaterialWipRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid ProductId { get; set; }
    public Guid AuthorizedByUserId { get; set; }
    public required string Reason { get; set; }
    public required string BeforeJson { get; set; }
    public required string AfterJson { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Product Product { get; set; } = null!;
    public User AuthorizedByUser { get; set; } = null!;
}

public sealed class ProductionProcessWipTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductionStageId { get; set; }
    public Guid? LocationId { get; set; }
    public string? RowCode { get; set; }
    public short? RackNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProductionStage ProductionStage { get; set; } = null!;
    public Location? Location { get; set; }
}

public sealed class ProductionProcessConfiguration
{
    public short Id { get; set; } = 1;
    public uint Version { get; set; }
}

public sealed class ProductionProcessRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid ProductionStageId { get; set; }
    public Guid AuthorizedByUserId { get; set; }
    public required string Reason { get; set; }
    public required string BeforeJson { get; set; }
    public required string AfterJson { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public ProductionStage ProductionStage { get; set; } = null!;
    public User AuthorizedByUser { get; set; } = null!;
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
    public decimal OriginalTargetQuantity { get; set; }
    public DateTimeOffset? PrincipalClosedAt { get; set; }
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
    public bool UsesBatchTraceability { get; set; }
    public bool UsesSupplyRequests { get; set; }
    public ProductionSupplyPriority SupplyPriority { get; set; } = ProductionSupplyPriority.Normal;
    public int? RecipeVersion { get; set; }
    public Product Product { get; set; } = null!;
    public Unit Unit { get; set; } = null!;
    public User CreatedByUser { get; set; } = null!;
    public ICollection<ProductionWorkOrderStage> Stages { get; set; } = [];
    public ICollection<ProductionEvent> Events { get; set; } = [];
    public ICollection<ProductionMaterialIssueLink> MaterialIssues { get; set; } = [];
    public ICollection<ProductionMaterialOperation> MaterialOperations { get; set; } = [];
    public ICollection<ProductionOrderMaterialPlan> MaterialPlan { get; set; } = [];
    public ICollection<ProductionOrderPlanningRevision> PlanningRevisions { get; set; } = [];
    public ICollection<ProductionBatch> Batches { get; set; } = [];
    public ICollection<ProductionSupplyRequest> SupplyRequests { get; set; } = [];
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
    public ICollection<ProductionMaterialIssueLink> MaterialIssues { get; set; } = [];
    public ICollection<ProductionOrderMaterialPlan> MaterialPlan { get; set; } = [];
}

public sealed class ProductionMaterialIssueLink
{
    public string PlateAllocationsJson { get; set; } = "[]";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkOrderId { get; set; }
    public Guid WorkOrderStageId { get; set; }
    public Guid? InventoryMovementLineId { get; set; }
    public Guid? SupplyRequestLineId { get; set; }
    public Guid ProductId { get; set; }
    public Guid WipLocationId { get; set; }
    public decimal Quantity { get; set; }
    public decimal CancelledQuantity { get; set; }
    public ProductionMaterialSupplySource Source { get; set; } = ProductionMaterialSupplySource.Transfer;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public ProductionWorkOrderStage WorkOrderStage { get; set; } = null!;
    public InventoryMovementLine? InventoryMovementLine { get; set; }
    public Product Product { get; set; } = null!;
    public Location WipLocation { get; set; } = null!;
    public ProductionSupplyRequestLine? SupplyRequestLine { get; set; }
    public ICollection<ProductionMaterialOperationLine> OperationLines { get; set; } = [];
    public ICollection<ProductionMaterialIssueLot> Lots { get; set; } = [];
}

public enum ProductionMaterialSupplySource { Transfer, WipAssignment }

public sealed class ProductionMaterialIssueLot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IssueLinkId { get; set; }
    public Guid LotId { get; set; }
    public decimal Quantity { get; set; }
    public ProductionMaterialIssueLink IssueLink { get; set; } = null!;
    public ProductLot Lot { get; set; } = null!;
}

public enum ProductionSupplyPriority { Normal, Urgent }
public enum ProductionSupplyRequestStatus { Pending, InProgress, Completed, Cancelled }
public enum ProductionSupplyEventType { Created, PreparationStarted, PreparationContinued, StockReserved, ProblemReported, QuantityCancelled, PriorityChanged, Delivered, DeliveryReversed, PreparationSaved, PreparationDiscarded, DestinationChanged, WipAssigned, WipAssignmentCancelled }
public enum ProductionSupplyPreparationStatus { Open, Confirmed, Discarded }
public enum ProductionSupplySourceKind { Warehouse, ExistingWip }

public sealed class ProductionSupplyRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkOrderId { get; set; }
    public Guid WorkOrderStageId { get; set; }
    public string DestinationCode { get; set; } = string.Empty;
    public Guid? DestinationLocationId { get; set; }
    public ProductionSupplyRequestStatus Status { get; set; } = ProductionSupplyRequestStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public uint Version { get; set; }
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public ProductionWorkOrderStage WorkOrderStage { get; set; } = null!;
    public Location? DestinationLocation { get; set; }
    public ICollection<ProductionSupplyRequestLine> Lines { get; set; } = [];
    public ICollection<ProductionSupplyEvent> Events { get; set; } = [];
}

public sealed class ProductionSupplyRequestLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplyRequestId { get; set; }
    public Guid? ReworkCaseId { get; set; }
    public ProductionReworkCase? ReworkCase { get; set; }
    public Guid MaterialPlanId { get; set; }
    public Guid ProductId { get; set; }
    public short UnitId { get; set; }
    public decimal RequiredQuantity { get; set; }
    public decimal CancelledQuantity { get; set; }
    public decimal ReopenedQuantity { get; set; }
    public Guid? DestinationLocationId { get; set; }
    public string? DestinationCode { get; set; }
    public ProductionSupplyRequest SupplyRequest { get; set; } = null!;
    public ProductionOrderMaterialPlan MaterialPlan { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public Unit Unit { get; set; } = null!;
    public Location? DestinationLocation { get; set; }
    public ICollection<ProductionWarehouseReservation> Reservations { get; set; } = [];
    public ICollection<ProductionMaterialIssueLink> IssueLinks { get; set; } = [];
    public ICollection<ProductionSupplyPreparation> Preparations { get; set; } = [];
}

public sealed class ProductionSupplyPreparation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid SupplyRequestLineId { get; set; }
    public Guid DestinationLocationId { get; set; }
    public ProductionSupplyPreparationStatus Status { get; set; } = ProductionSupplyPreparationStatus.Open;
    public Guid ResponsibleUserId { get; set; }
    public uint Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ProductionSupplyRequestLine SupplyRequestLine { get; set; } = null!;
    public Location DestinationLocation { get; set; } = null!;
    public User ResponsibleUser { get; set; } = null!;
    public ICollection<ProductionSupplyPreparationSource> Sources { get; set; } = [];
}

public sealed class ProductionSupplyPreparationSource
{
    public string PlatesJson { get; set; } = "[]";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PreparationId { get; set; }
    public ProductionSupplySourceKind Kind { get; set; }
    public Guid LocationId { get; set; }
    public decimal Quantity { get; set; }
    public ProductionSupplyPreparation Preparation { get; set; } = null!;
    public Location Location { get; set; } = null!;
}

public sealed class ProductionSupplyConfirmation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid SupplyRequestLineId { get; set; }
    public Guid PreparationId { get; set; }
    public Guid DestinationLocationId { get; set; }
    public decimal Quantity { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public ProductionSupplyRequestLine SupplyRequestLine { get; set; } = null!;
    public ProductionSupplyPreparation Preparation { get; set; } = null!;
    public Location DestinationLocation { get; set; } = null!;
    public User ResponsibleUser { get; set; } = null!;
    public ICollection<ProductionSupplyConfirmationMovement> Movements { get; set; } = [];
    public ICollection<ProductionSupplyConfirmationIssue> Issues { get; set; } = [];
}

public sealed class ProductionSupplyConfirmationMovement
{
    public Guid ConfirmationId { get; set; }
    public Guid InventoryMovementId { get; set; }
    public ProductionSupplyConfirmation Confirmation { get; set; } = null!;
    public InventoryMovement InventoryMovement { get; set; } = null!;
}

public sealed class ProductionSupplyConfirmationIssue
{
    public Guid ConfirmationId { get; set; }
    public Guid IssueLinkId { get; set; }
    public ProductionSupplyConfirmation Confirmation { get; set; } = null!;
    public ProductionMaterialIssueLink IssueLink { get; set; } = null!;
}

public sealed class ProductionWarehouseReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplyRequestLineId { get; set; }
    public Guid LocationId { get; set; }
    public Guid LotId { get; set; }
    public decimal Quantity { get; set; }
    public decimal ReleasedQuantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProductionSupplyRequestLine SupplyRequestLine { get; set; } = null!;
    public Location Location { get; set; } = null!;
    public ProductLot Lot { get; set; } = null!;
}

public sealed class ProductionSupplyEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid SupplyRequestId { get; set; }
    public Guid? SupplyRequestLineId { get; set; }
    public ProductionSupplyEventType Type { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public decimal Quantity { get; set; }
    public string? Reason { get; set; }
    public Guid? InventoryMovementId { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProductionSupplyRequest SupplyRequest { get; set; } = null!;
    public ProductionSupplyRequestLine? SupplyRequestLine { get; set; }
    public User ResponsibleUser { get; set; } = null!;
    public InventoryMovement? InventoryMovement { get; set; }
}

public sealed class ProductionMaterialOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid WorkOrderStageId { get; set; }
    public ProductionMaterialOperationType Type { get; set; }
    public ProductionMaterialReturnEffect? ReturnEffect { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public Guid? ReversesOperationId { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public ProductionWorkOrderStage WorkOrderStage { get; set; } = null!;
    public User ResponsibleUser { get; set; } = null!;
    public ProductionMaterialOperation? ReversesOperation { get; set; }
    public ICollection<ProductionMaterialOperationLine> Lines { get; set; } = [];
}

public sealed class ProductionMaterialOperationLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductionMaterialOperationId { get; set; }
    public Guid IssueLinkId { get; set; }
    public Guid InventoryMovementLineId { get; set; }
    public decimal Quantity { get; set; }
    public ProductionMaterialOperation Operation { get; set; } = null!;
    public ProductionMaterialIssueLink IssueLink { get; set; } = null!;
    public InventoryMovementLine InventoryMovementLine { get; set; } = null!;
}

public sealed class ProductionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid? WorkOrderStageId { get; set; }
    public Guid? RelatedStageId { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? RelatedEventId { get; set; }
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
    public ProductionBatch? Batch { get; set; }
    public ProductionEvent? RelatedEvent { get; set; }
    public User ResponsibleUser { get; set; } = null!;
    public ProductionShift? Shift { get; set; }
    public InventoryMovement? InventoryMovement { get; set; }
}
