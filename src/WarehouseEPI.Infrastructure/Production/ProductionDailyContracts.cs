using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public enum ProductionDailyCommandStatus
{
    Success,
    NotFound,
    ValidationFailed,
    ConcurrencyConflict,
    IdempotencyConflict,
    InvalidPin
}

public sealed record ProductionDailyCommandResult(
    ProductionDailyCommandStatus Status,
    Guid? Id = null,
    IReadOnlyList<string>? Errors = null)
{
    public bool Success => Status == ProductionDailyCommandStatus.Success;
}

public sealed record ConfigureProductionDailyCommand(
    Guid OperationId,
    uint ExpectedVersion,
    Guid CuttingStageId,
    Guid SewingStageId,
    Guid ReadyToPackStageId,
    Guid Shift1Id,
    Guid Shift2Id,
    Guid ActorUserId);

public sealed record CreateProductionScheduleWeekCommand(
    Guid OperationId,
    DateOnly WeekStart,
    Guid ActorUserId);

public sealed record SaveProductionScheduleLineCommand(
    Guid OperationId,
    Guid WeekId,
    Guid? LineId,
    uint ExpectedWeekVersion,
    uint? ExpectedLineVersion,
    DateOnly PlannedDate,
    Guid ProductId,
    decimal Quantity,
    string? OrderReference1,
    string? OrderReference2,
    string? OrderReference3,
    string? Notes,
    Guid ActorUserId,
    string AdminPin = "",
    string? OriginalType = null,
    string? OriginalAnnotation1 = null,
    string? OriginalAnnotation2 = null,
    string? OriginalAnnotation1Kind = null,
    string? OriginalAnnotation2Kind = null);

public sealed record ProductionScheduleBatchLine(DateOnly PlannedDate, Guid ProductId, decimal Quantity,
    string? OrderReference1, string? OrderReference2, string? OrderReference3, string? Notes,
    string? OriginalType = null, string? OriginalAnnotation1 = null, string? OriginalAnnotation2 = null,
    string? OriginalAnnotation1Kind = null, string? OriginalAnnotation2Kind = null);

public sealed record SaveProductionScheduleBatchCommand(Guid OperationId, Guid WeekId,
    uint ExpectedWeekVersion, IReadOnlyList<ProductionScheduleBatchLine> Lines,
    Guid ActorUserId, string AdminPin = "");

public sealed record ProductionScheduleDraftChange(string Kind, Guid? LineId,
    uint? ExpectedLineVersion, ProductionScheduleBatchLine? Line);

public sealed record SaveProductionScheduleDraftCommand(Guid OperationId, Guid WeekId,
    uint ExpectedWeekVersion, IReadOnlyList<ProductionScheduleDraftChange> Changes,
    Guid ActorUserId, IReadOnlyList<ProductionOpeningChange>? Openings = null);

public sealed record CancelProductionScheduleLineCommand(Guid OperationId, Guid WeekId, Guid LineId,
    uint ExpectedWeekVersion, uint ExpectedLineVersion, Guid ActorUserId, string AdminPin = "");

public sealed record ProductionScheduleLineDeletion(Guid LineId, bool Allowed, bool RequiresPin, string? Reason);

public sealed record PublishProductionScheduleWeekCommand(
    Guid OperationId,
    Guid WeekId,
    uint ExpectedVersion,
    string AdminPin,
    Guid ActorUserId);

public sealed record ChangeProductionScheduleWeekStatusCommand(
    Guid OperationId,
    Guid WeekId,
    uint ExpectedVersion,
    Guid ActorUserId);

public sealed record ProductionScheduleLineView(
    Guid Id,
    int Sequence,
    DateOnly PlannedDate,
    Guid ProductId,
    string Sku,
    string? Description,
    decimal Quantity,
    string? OrderReference1,
    string? OrderReference2,
    string? OrderReference3,
    string? Notes,
    bool IsCarryover,
    ProductionDailyArea? StartArea,
    Guid? WorkOrderId,
    uint Version,
    string? Unit = null,
    string? OriginalType = null,
    string? OriginalAnnotation1 = null,
    string? OriginalAnnotation2 = null,
    string? OriginalAnnotation1Kind = null,
    string? OriginalAnnotation2Kind = null);

public sealed record ProductionScheduleWeekView(
    Guid Id,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    ProductionScheduleWeekStatus Status,
    ProductionScheduleOrigin Origin,
    uint Version,
    IReadOnlyList<ProductionScheduleLineView> Lines, bool ExplicitCarryover = false);

public sealed record ProductionDailyConfigurationView(
    Guid? CuttingStageId,
    Guid? SewingStageId,
    Guid? ReadyToPackStageId,
    Guid? Shift1Id,
    Guid? Shift2Id,
    uint Version,
    bool IsComplete);

public sealed record PreviewProductionDailyCaptureCommand(
    DateOnly EffectiveDate,
    ProductionDailyArea Area,
    Guid ShiftId,
    Guid ProductId,
    decimal Quantity);

public sealed record ConfirmProductionDailyCaptureCommand(
    Guid OperationId,
    DateOnly EffectiveDate,
    ProductionDailyArea Area,
    Guid ShiftId,
    Guid ProductId,
    decimal Quantity,
    string? Notes,
    string Pin);

public sealed record ReverseProductionDailyCaptureCommand(
    Guid OperationId,
    Guid CaptureId,
    string Reason,
    string AdminPin);

public sealed record ProductionDailyAllocationPreview(
    Guid ScheduleLineId,
    Guid WorkOrderId,
    Guid WorkOrderStageId,
    Guid BatchId,
    DateOnly PlannedDate,
    string OrderNumber,
    decimal Quantity,
    IReadOnlyList<string> References);

public sealed record ProductionDailyCapturePreview(
    bool CanConfirm,
    Guid? WeekId,
    Guid? StageId,
    string? Product,
    decimal Requested,
    IReadOnlyList<ProductionDailyAllocationPreview> Allocations,
    IReadOnlyList<string> Blockers,
    decimal Available = 0, decimal Extra = 0, decimal ToReconcile = 0, string StateFingerprint = "");

public sealed record ProductionDailyAreaBalance(
    ProductionDailyArea Area,
    bool Applies,
    decimal Completed,
    decimal Pending,
    decimal Advance, decimal Extra = 0, decimal ToReconcile = 0, decimal Opening = 0, decimal? NetPending = null,
    decimal CompletedShift1 = 0, decimal CompletedShift2 = 0, decimal PendingAfterShift1 = 0);

public sealed record ProductionDailyBalanceRow(
    DateOnly Date,
    Guid ProductId,
    string Sku,
    string? Description,
    decimal NewPlan,
    decimal Carryover,
    decimal CumulativePlan,
    decimal Shift1Completed,
    decimal Shift2Completed,
    IReadOnlyList<string> References,
    ProductionDailyAreaBalance Cutting,
    ProductionDailyAreaBalance Sewing,
    ProductionDailyAreaBalance ReadyToPack,
    decimal ProgressPercent);

public sealed record ProductionDailyBalanceView(
    Guid WeekId,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    ProductionScheduleWeekStatus Status,
    IReadOnlyList<ProductionDailyBalanceRow> Rows);

public sealed record ProductionDailyCaptureDetail(
    Guid Id,
    DateOnly EffectiveDate,
    ProductionDailyArea Area,
    string Shift,
    string Sku,
    decimal Quantity,
    string Responsible,
    DateTimeOffset RecordedAt,
    ProductionDailyCaptureStatus Status,
    string? Notes,
    IReadOnlyList<string> Orders);
