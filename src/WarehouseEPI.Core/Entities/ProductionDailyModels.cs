namespace WarehouseEPI.Core.Entities;

public enum ProductionScheduleWeekStatus { Draft, Open, Closed }
public enum ProductionScheduleOrigin { Manual, ExcelImport }
public enum ProductionDailyArea { Cutting, Sewing, ReadyToPack }
public enum ProductionDailyCaptureStatus { Active, Reversed }

public sealed class ProductionDailyConfiguration
{
    public short Id { get; set; } = 1;
    public Guid? LastOperationId { get; set; }
    public string? LastRequestFingerprint { get; set; }
    public Guid? CuttingStageId { get; set; }
    public Guid? SewingStageId { get; set; }
    public Guid? ReadyToPackStageId { get; set; }
    public Guid? Shift1Id { get; set; }
    public Guid? Shift2Id { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public uint Version { get; set; }
    public ProductionStage? CuttingStage { get; set; }
    public ProductionStage? SewingStage { get; set; }
    public ProductionStage? ReadyToPackStage { get; set; }
    public ProductionShift? Shift1 { get; set; }
    public ProductionShift? Shift2 { get; set; }
    public User? UpdatedByUser { get; set; }
}

public sealed class ProductionScheduleWeek
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public DateOnly WeekStart { get; set; }
    public DateOnly WeekEnd { get; set; }
    public ProductionScheduleWeekStatus Status { get; set; } = ProductionScheduleWeekStatus.Draft;
    public ProductionScheduleOrigin Origin { get; set; } = ProductionScheduleOrigin.Manual;
    public bool ExplicitCarryover { get; set; }
    public string? SourceName { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? PublishedByUserId { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public uint Version { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public User? PublishedByUser { get; set; }
    public User? ClosedByUser { get; set; }
    public ICollection<ProductionScheduleLine> Lines { get; set; } = [];
    public ICollection<ProductionDailyCapture> Captures { get; set; } = [];
    public ICollection<ProductionScheduleRevision> Revisions { get; set; } = [];
}

public sealed class ProductionScheduleLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WeekId { get; set; }
    public int Sequence { get; set; }
    public DateOnly PlannedDate { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public string? OrderReference1 { get; set; }
    public string? OrderReference2 { get; set; }
    public string? OrderReference3 { get; set; }
    public string? Notes { get; set; }
    public string? OriginalType { get; set; }
    public string? OriginalAnnotation1 { get; set; }
    public string? OriginalAnnotation2 { get; set; }
    public string? OriginalAnnotation1Kind { get; set; }
    public string? OriginalAnnotation2Kind { get; set; }
    public ProductionScheduleOrigin Origin { get; set; } = ProductionScheduleOrigin.Manual;
    public bool IsExtra { get; set; }
    public bool IsCarryover { get; set; }
    public ProductionDailyArea? StartArea { get; set; }
    public Guid? WorkOrderId { get; set; }
    public string? SourceSheet { get; set; }
    public int? SourceRow { get; set; }
    public bool IsCancelled { get; set; }
    public uint Version { get; set; }
    public ProductionScheduleWeek Week { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public ProductionWorkOrder? WorkOrder { get; set; }
    public ICollection<ProductionDailyCaptureAllocation> CaptureAllocations { get; set; } = [];
}

public sealed class ProductionScheduleRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid WeekId { get; set; }
    public Guid? LineId { get; set; }
    public required string Action { get; set; }
    public required string BeforeJson { get; set; }
    public required string AfterJson { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public ProductionScheduleWeek Week { get; set; } = null!;
    public User ResponsibleUser { get; set; } = null!;
}

public sealed class ProductionDailyCapture
{
    public bool IsFlexible { get; set; }
    public Guid? ReverseOperationId { get; set; }
    public string? ReverseFingerprint { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid WeekId { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public ProductionDailyArea Area { get; set; }
    public Guid StageId { get; set; }
    public Guid ShiftId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public string? Notes { get; set; }
    public ProductionScheduleOrigin Origin { get; set; } = ProductionScheduleOrigin.Manual;
    public string? ImportedReporter { get; set; }
    public string? SourceSheet { get; set; }
    public int? SourceRow { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public ProductionDailyCaptureStatus Status { get; set; } = ProductionDailyCaptureStatus.Active;
    public Guid? ReversedByUserId { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public string? ReverseReason { get; set; }
    public ProductionScheduleWeek Week { get; set; } = null!;
    public ProductionStage Stage { get; set; } = null!;
    public ProductionShift Shift { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public User ResponsibleUser { get; set; } = null!;
    public User? ReversedByUser { get; set; }
    public ICollection<ProductionDailyCaptureAllocation> Allocations { get; set; } = [];
}

public sealed class ProductionDailyCaptureAllocation
{
    public DateTimeOffset? ReconciledAt { get; set; }
    public Guid? ReconciledByUserId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaptureId { get; set; }
    public Guid ScheduleLineId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid WorkOrderStageId { get; set; }
    public Guid? BatchResultId { get; set; }
    public decimal Quantity { get; set; }
    public Guid ProcessOperationId { get; set; }
    public Guid? DeliveryOperationId { get; set; }
    public Guid? ReceiveOperationId { get; set; }
    public ProductionDailyCapture Capture { get; set; } = null!;
    public ProductionScheduleLine ScheduleLine { get; set; } = null!;
    public ProductionWorkOrder WorkOrder { get; set; } = null!;
    public ProductionWorkOrderStage WorkOrderStage { get; set; } = null!;
    public ProductionBatchResult? BatchResult { get; set; }
}

public sealed class ProductionScheduleImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public required string FileHash { get; set; }
    public required string FileName { get; set; }
    public Guid ImportedByUserId { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public int WeekCount { get; set; }
    public int LineCount { get; set; }
    public int CaptureCount { get; set; }
    // Links, corrections and skipped rows applied in the importer, one per line; null when the file imported as-is.
    public string? ResolutionSummary { get; set; }
    public User ImportedByUser { get; set; } = null!;
}
