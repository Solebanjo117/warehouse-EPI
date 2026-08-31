namespace WarehouseEPI.Core.Entities;

public enum ReceivingDocumentStatus
{
    Open,
    PartiallyReceived,
    Completed,
    ClosedWithDifferences,
    Cancelled
}
