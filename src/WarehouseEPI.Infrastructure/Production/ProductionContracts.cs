using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Infrastructure.Production;

public enum ProductionCommandStatus { Success, InvalidPin, Forbidden, NotFound, ValidationFailed, ConcurrencyConflict, IdempotencyConflict, RequiresLocationSharingConfirmation }
public sealed record ProductionCommandResult(ProductionCommandStatus Status, Guid? WorkOrderId = null, Guid? MovementId = null, IReadOnlyList<string>? Errors = null, IReadOnlyList<SharedLocationConflict>? Conflicts = null)
{
    public IReadOnlyList<string> ValidationErrors => Errors ?? [];
}
public sealed record CreateProductionRouteCommand(Guid OperationId, Guid ProductId, string Name, IReadOnlyList<Guid> StageIds, string Pin);
public sealed record CreateProductionOrderCommand(Guid OperationId, Guid ProductId, decimal TargetQuantity, string? ExternalReference, DateOnly? DueDate, string? Notes, string Pin);
public sealed record ProductionOrderActionCommand(Guid OperationId, Guid WorkOrderId, uint? ExpectedVersion, string Pin, string? Reason = null, decimal Quantity = 0);
public sealed record RecordProductionCommand(Guid OperationId, Guid WorkOrderId, Guid StageId, Guid ShiftId, bool IsRework, decimal InputQuantity, decimal GoodQuantity, decimal ReworkQuantity, decimal ScrapQuantity, string? Reason, string Pin);
public sealed record ProductionHandoffCommand(Guid OperationId, Guid WorkOrderId, Guid SourceStageId, Guid TargetStageId, decimal Quantity, string Pin, string? Reason = null, Guid? BatchId = null, Guid? DeliveryEventId = null, uint? ExpectedVersion = null);
public sealed record ReceiveProductionToWarehouseCommand(Guid OperationId, Guid WorkOrderId, Guid FinalStageId, decimal Quantity, Guid DestinationLocationId, string Pin, IReadOnlyCollection<SharedAssignmentApproval>? ApprovedSharedAssignments = null, Guid? BatchId = null, uint? ExpectedVersion = null);
public sealed record ProductionStageProgress(Guid Id, int Sequence, string Code, string Name, decimal AvailableInput, decimal Processed, decimal Good, decimal Rework, decimal Scrap, decimal AvailableToDeliver, decimal Delivered, decimal Received, decimal PendingReceipt);
public sealed record ProductionEventItem(ProductionEventType Type, string? Stage, string? RelatedStage, decimal Quantity, decimal Good, decimal Rework, decimal Scrap, string Responsible, string? Shift, string? Reason, DateTimeOffset RecordedAt, Guid? MovementId);
public sealed record ProductionOrderDetail(Guid Id, string Number, string? ExternalReference, string Sku, string? Description, string Unit, bool AllowsDecimals, decimal TargetQuantity, decimal AuthorizedQuantity, DateOnly? DueDate, string? Notes, ProductionWorkOrderStatus Status, uint Version, decimal WarehouseReceived, IReadOnlyList<ProductionStageProgress> Stages, IReadOnlyList<ProductionEventItem> Events, bool UsesBatchTraceability, int? RecipeVersion);
public sealed record ProductionOrderRow(Guid Id, string Number, string? ExternalReference, string Sku, string? Description,
    decimal Target, decimal Received, string Unit, ProductionWorkOrderStatus Status, DateOnly? DueDate,
    string? ActiveProcess = null, int AlertCount = 0, bool UsesBatchTraceability = false);
public sealed record ProductionOrderSearch(string? Search, ProductionWorkOrderStatus? Status, Guid? ActiveStageId,
    DateOnly? From, DateOnly? To, bool AlertsOnly, int Page = 1, int PageSize = 25);
public sealed record ProductionOrderPage(IReadOnlyList<ProductionOrderRow> Items, int Page, int PageSize,
    int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (decimal)PageSize));
}
