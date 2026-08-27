namespace WarehouseEPI.Core.Entities;

public sealed class LocationRackRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public required string RowCode { get; set; }
    public short RackNumber { get; set; }
    public required string Reason { get; set; }
    public required string BeforeJson { get; set; }
    public required string AfterJson { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid AuthorizedByUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    public User RequestedByUser { get; set; } = null!;
    public User AuthorizedByUser { get; set; } = null!;
}
