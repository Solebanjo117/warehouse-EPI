namespace WarehouseEPI.Core.Entities;

public sealed class ProductionCaptureSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid ResponsibleUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public ICollection<ProductionCaptureSubmissionItem> Items { get; set; } = [];
}

public sealed class ProductionCaptureSubmissionItem
{
    public Guid SubmissionId { get; set; }
    public Guid CaptureId { get; set; }
    public ProductionCaptureSubmission Submission { get; set; } = null!;
    public ProductionDailyCapture Capture { get; set; } = null!;
}

public sealed class ProductionCarryoverPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WeekId { get; set; }
    public DateOnly PlannedDate { get; set; }
    public Guid ProductId { get; set; }
    public ProductionDailyArea Area { get; set; }
    public decimal Quantity { get; set; }
    public uint Version { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
