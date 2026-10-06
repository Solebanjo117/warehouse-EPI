using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

// Compatibility facade: all new dispositions share the documentary ledger with production.
public sealed class WipDispositionService(WarehouseDbContext dbContext, UserPinService userPinService, TimeProvider timeProvider)
{
    public async Task<WipDispositionResult> ConfirmAsync(WipDispositionCommand command, CancellationToken cancellationToken = default)
    {
        var document = await dbContext.WipDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.MovementLineId == command.OriginalMovementLineId, cancellationToken);
        if (document is null) return new(WipDispositionStatus.ValidationFailed, Errors: ["La entrega requiere conversión documental antes de registrar devoluciones."]);
        if (command.Type is not (WipDispositionType.WarehouseReturn or WipDispositionType.SupplierReturn))
            return new(WipDispositionStatus.ValidationFailed);
        await using var transaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var result = await new WipDocumentService(dbContext, userPinService, timeProvider).ConfirmAsync(new(
            command.OperationId, document.ProductId, document.WipLocationId, command.Quantity,
            command.Type == WipDispositionType.WarehouseReturn ? WipDocumentApplicationKind.WarehouseReturn : WipDocumentApplicationKind.SupplierReturn,
            command.Pin, command.DestinationLocationId, document.Id, Reference: command.Reference, Notes: command.Notes,
            ApproveSharedDestination: command.ApprovedSharedAssignments?.Any(x => x.ProductId == document.ProductId && x.LocationId == command.DestinationLocationId) == true), cancellationToken);
        if (result.Status != InventoryMovementStatus.Success)
            return new(result.Status == InventoryMovementStatus.InvalidPin ? WipDispositionStatus.InvalidPin :
                result.Status == InventoryMovementStatus.RoleNotAllowed ? WipDispositionStatus.RoleNotAllowed :
                result.Status == InventoryMovementStatus.RequiresLocationSharingConfirmation ? WipDispositionStatus.RequiresLocationSharingConfirmation :
                result.Status == InventoryMovementStatus.IdempotencyConflict ? WipDispositionStatus.IdempotencyConflict : WipDispositionStatus.ValidationFailed,
                SharingConflicts: result.Conflicts, Errors: result.ValidationErrors);
        var existing = await dbContext.WipDispositions.SingleOrDefaultAsync(x => x.OperationId == command.OperationId, cancellationToken);
        if (existing is null)
        {
            existing = new WipDisposition
            {
                OperationId = command.OperationId,
                RequestFingerprint = "documentary:" + command.OperationId,
                OriginalMovementLineId = command.OriginalMovementLineId,
                Type = command.Type,
                Quantity = command.Quantity,
                ResponsibleUserId = result.ResponsibleUserId!.Value,
                DestinationLocationId = command.DestinationLocationId,
                InventoryMovementId = result.MovementId,
                Reference = command.Reference,
                Notes = command.Notes,
                OccurredAt = timeProvider.GetUtcNow(),
                RecordedAt = timeProvider.GetUtcNow()
            };
            dbContext.WipDispositions.Add(existing); await dbContext.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(WipDispositionStatus.Success, existing.Id, result.MovementId);
    }
}
