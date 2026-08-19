namespace WarehouseEPI.Core.Entities;

public sealed class WarehouseMapRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public int PreviousVersion { get; set; }
    public int NewVersion { get; set; }
    public string? Reason { get; set; }
    public required string ChangesJson { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid AuthorizedByUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public User RequestedByUser { get; set; } = null!;
    public User AuthorizedByUser { get; set; } = null!;
}
