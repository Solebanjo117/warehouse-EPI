using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record WipDispositionCorrectionCommand(
    Guid OperationId,
    Guid DispositionId,
    Guid RequestedByUserId,
    string Pin,
    string Reason);

public sealed class WipDispositionCorrectionService(
    WarehouseDbContext dbContext,
    UserPinService userPinService,
    TimeProvider timeProvider)
{
    private readonly InventoryReversalService reversalService = new(dbContext, timeProvider);

    public async Task<WipDispositionResult> ReverseAsync(
        WipDispositionCorrectionCommand command,
        CancellationToken cancellationToken = default)
    {
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (command.OperationId == Guid.Empty || command.DispositionId == Guid.Empty ||
            command.RequestedByUserId == Guid.Empty || reason.Length is 0 or > 500)
            return new(WipDispositionStatus.ValidationFailed, Errors: ["La devolución y el motivo son obligatorios."]);

        var requester = await dbContext.Users.AsNoTracking().Include(user => user.Role)
            .SingleOrDefaultAsync(user => user.Id == command.RequestedByUserId, cancellationToken);
        if (requester is null || !requester.IsActive || requester.Role.Code != "ADMIN")
            return new(WipDispositionStatus.ValidationFailed, Errors: ["Solo un administrador puede corregir devoluciones WIP."]);
        var authorized = await userPinService.AuthenticateAsync(command.Pin, cancellationToken);
        if (authorized is null || authorized.Role.Code != "ADMIN")
            return new(WipDispositionStatus.InvalidPin);

        var fingerprint = InventoryFingerprint.Hash(
            $"{command.DispositionId:N}|{command.RequestedByUserId:N}|{authorized.Id:N}|{reason}");
        var existing = await dbContext.WipDispositions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == command.OperationId, cancellationToken);
        if (existing is not null)
            return existing.RequestFingerprint == fingerprint
                ? new(WipDispositionStatus.Success, existing.Id, existing.InventoryMovementId)
                : new(WipDispositionStatus.IdempotencyConflict);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        if (transaction is not null)
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM wip_dispositions WHERE id = {command.DispositionId} FOR UPDATE", cancellationToken);
        var original = await dbContext.WipDispositions
            .Include(item => item.InventoryMovement).ThenInclude(movement => movement!.Lines)
                .ThenInclude(line => line.BalanceChanges)
            .SingleOrDefaultAsync(item => item.Id == command.DispositionId, cancellationToken);
        if (original is null || original.ReversesDispositionId is not null ||
            await dbContext.WipDispositions.AnyAsync(item => item.ReversesDispositionId == command.DispositionId, cancellationToken))
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return new(WipDispositionStatus.ValidationFailed, Errors: ["La devolución no existe o ya fue corregida."]);
        }

        InventoryMovement? inventoryReversal = null;
        if (original.InventoryMovement is not null)
        {
            inventoryReversal = await reversalService.CreateAsync(
                original.InventoryMovement, authorized.Id, fingerprint, cancellationToken);
            dbContext.InventoryMovementCorrections.Add(new InventoryMovementCorrection
            {
                OperationId = Guid.NewGuid(),
                RequestFingerprint = InventoryFingerprint.Hash(fingerprint + "|movement-correction"),
                Type = InventoryMovementCorrectionType.Reversal,
                OriginalMovementId = original.InventoryMovement.Id,
                ReversalMovement = inventoryReversal,
                Reason = reason,
                RequestedByUserId = requester.Id,
                AuthorizedByUserId = authorized.Id,
                RecordedAt = timeProvider.GetUtcNow()
            });
        }

        var reversal = new WipDisposition
        {
            OperationId = command.OperationId,
            RequestFingerprint = fingerprint,
            OriginalMovementLineId = original.OriginalMovementLineId,
            Type = original.Type,
            Quantity = original.Quantity,
            ResponsibleUserId = authorized.Id,
            DestinationLocationId = original.DestinationLocationId,
            InventoryMovement = inventoryReversal,
            ReversesDispositionId = original.Id,
            Reference = original.Reference,
            Notes = "Corrección: " + reason,
            OccurredAt = timeProvider.GetUtcNow(),
            RecordedAt = timeProvider.GetUtcNow()
        };
        dbContext.WipDispositions.Add(reversal);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(WipDispositionStatus.Success, reversal.Id, inventoryReversal?.Id);
    }
}
