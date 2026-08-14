namespace WarehouseEPI.Core.Entities;

public sealed class ProductLotDateChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid ProductLotId { get; set; }
    public DateOnly? PreviousLotDate { get; set; }
    public DateOnly? NewLotDate { get; set; }
    public required string Reason { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid AuthorizedByUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    public ProductLot ProductLot { get; set; } = null!;
    public User RequestedByUser { get; set; } = null!;
    public User AuthorizedByUser { get; set; } = null!;
}
