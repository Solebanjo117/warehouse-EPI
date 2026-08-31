using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record OpenReceivingDocumentCommand(
    Guid OperationId,
    ReceivingDocumentType Type,
    string Number,
    string Origin,
    DateOnly? DocumentDate,
    string? Notes,
    string Pin,
    IReadOnlyList<OpenReceivingDocumentLineCommand> Lines);

public sealed record OpenReceivingDocumentLineCommand(Guid ProductId, decimal ExpectedQuantity);

public sealed record ConfirmReceivingCommand(
    Guid OperationId,
    Guid DocumentId,
    string Pin,
    IReadOnlyList<ConfirmReceivingLineCommand> Lines,
    bool DifferenceAcknowledged = false,
    string? DifferenceNotes = null,
    IReadOnlyCollection<SharedAssignmentApproval>? ApprovedSharedAssignments = null);

public sealed record ConfirmReceivingLineCommand(
    Guid ProductId,
    decimal Quantity,
    Guid DestinationLocationId,
    string? ExternalLotReference = null);

public sealed record CompleteReceivingDocumentCommand(Guid OperationId, Guid DocumentId, string Pin, string Reason);

public enum ReceivingCommandStatus
{
    Success,
    InvalidPin,
    ValidationFailed,
    NotFound,
    InvalidState,
    RequiresDifferenceAcknowledgement,
    RequiresLocationSharingConfirmation,
    BalanceChanged,
    ConcurrencyConflict,
    IdempotencyConflict
}

public sealed record ReceivingCommandResult(
    ReceivingCommandStatus Status,
    Guid? DocumentId = null,
    Guid? MovementId = null,
    ReceivingDocumentStatus? DocumentStatus = null,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<SharedLocationConflict>? SharingConflicts = null)
{
    public IReadOnlyList<string> ValidationErrors => Errors ?? [];
    public IReadOnlyList<SharedLocationConflict> Conflicts => SharingConflicts ?? [];
}

public sealed record ReceivingDocumentFilter(string? Search, ReceivingDocumentStatus? Status, int Page = 1, int PageSize = 25);
public sealed record ReceivingDocumentListRow(Guid Id, ReceivingDocumentType Type, string Number, string Origin, ReceivingDocumentStatus Status, DateOnly? DocumentDate, DateTimeOffset OpenedAt, int ProductCount, decimal ProgressPercent);
public sealed record ReceivingDocumentPage(IReadOnlyList<ReceivingDocumentListRow> Items, int TotalCount);
public sealed record ReceivingDocumentLineDetail(Guid Id, Guid ProductId, string Sku, string? Description, string Unit, decimal Expected, decimal Received, decimal Difference);
public sealed record ReceivingConfirmationLineDetail(string Sku, decimal Quantity, string Unit, string Destination, string? ExternalLotReference, bool Unexpected);
public sealed record ReceivingConfirmationDetail(Guid Id, Guid MovementId, DateTimeOffset OccurredAt, string Responsible, string? DifferenceNotes, IReadOnlyList<ReceivingConfirmationLineDetail> Lines);
public sealed record ReceivingDocumentEventDetail(ReceivingDocumentEventType Type, DateTimeOffset RecordedAt, string Actor, string? Notes);
public sealed record ReceivingDocumentDetail(Guid Id, ReceivingDocumentType Type, string Number, string Origin, DateOnly? DocumentDate, ReceivingDocumentStatus Status, string? Notes, uint Version, string OpenedBy, DateTimeOffset OpenedAt, string? TerminalReason, IReadOnlyList<ReceivingDocumentLineDetail> Lines, IReadOnlyList<ReceivingConfirmationDetail> Confirmations, IReadOnlyList<ReceivingDocumentEventDetail> Events)
{
    public bool CanReceive => Status is ReceivingDocumentStatus.Open or ReceivingDocumentStatus.PartiallyReceived;
    public bool CanCancel => Status == ReceivingDocumentStatus.Open && Confirmations.Count == 0;
    public bool CanCloseWithDifferences => CanReceive && Confirmations.Count > 0;
}

public sealed record ReceivingMovementDocumentLink(Guid DocumentId, string DocumentLabel, ReceivingDocumentStatus Status, string? DifferenceNotes, IReadOnlyList<string> ExternalLotReferences);
