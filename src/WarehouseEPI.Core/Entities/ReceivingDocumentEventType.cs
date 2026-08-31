namespace WarehouseEPI.Core.Entities;

public enum ReceivingDocumentEventType
{
    Opened,
    ReceiptConfirmed,
    AutomaticallyCompleted,
    ClosedWithDifferences,
    Cancelled,
    ReceiptCorrected,
    ReopenedAfterCorrection
}
