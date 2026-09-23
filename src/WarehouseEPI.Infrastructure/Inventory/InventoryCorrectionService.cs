using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed class InventoryCorrectionService(
    WarehouseDbContext dbContext,
    UserPinService userPinService,
    InventoryMovementService movementService,
    TimeProvider timeProvider,
    WarehouseClock? warehouseClock = null,
    ReceivingService? receivingService = null)
{
    private readonly InventoryReversalService reversalService = new(dbContext, timeProvider, warehouseClock);

    public async Task<InventoryCorrectionResult> ConfirmAsync(
        InventoryCorrectionCommand command,
        CancellationToken cancellationToken = default)
    {
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (command.OperationId == Guid.Empty || command.OriginalMovementId == Guid.Empty ||
            command.RequestedByUserId == Guid.Empty || reason.Length is 0 or > 500)
        {
            return new(InventoryCorrectionStatus.ValidationFailed,
                Errors: ["La operación, el movimiento original y el motivo son obligatorios; el motivo admite hasta 500 caracteres."]);
        }

        var requestedBy = await dbContext.Users.AsNoTracking().Include(user => user.Role)
            .SingleOrDefaultAsync(user => user.Id == command.RequestedByUserId, cancellationToken);
        if (requestedBy is null || !requestedBy.IsActive || requestedBy.Role.Code != "ADMIN")
            return new(InventoryCorrectionStatus.Unauthorized);

        var authorizedBy = await userPinService.AuthenticateAsync(command.Pin, cancellationToken);
        if (authorizedBy is null || authorizedBy.Role.Code != "ADMIN")
            return new(InventoryCorrectionStatus.InvalidPin);

        var normalized = command with { Reason = reason, Replacement = Normalize(command.Replacement) };
        var fingerprint = CreateFingerprint(normalized, authorizedBy.Id);
        var existing = await ExistingAsync(normalized.OperationId, fingerprint, cancellationToken);
        if (existing is not null)
            return existing;

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var original = await dbContext.InventoryMovements
                .Include(movement => movement.Lines).ThenInclude(line => line.BalanceChanges)
                .SingleOrDefaultAsync(movement => movement.Id == normalized.OriginalMovementId, cancellationToken);
            if (original is null)
            {
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed,
                    Errors: ["El movimiento original no existe."]), cancellationToken);
            }

            if (await dbContext.InventoryMovementCorrections.AnyAsync(item => item.OriginalMovementId == original.Id, cancellationToken))
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.AlreadyCorrected), cancellationToken);
            if (await dbContext.InventoryMovementCorrections.AnyAsync(item => item.ReversalMovementId == original.Id, cancellationToken))
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.CannotCorrectReversal), cancellationToken);
            var isDocumentReceipt = await dbContext.ReceivingConfirmations.AsNoTracking()
                .AnyAsync(item => item.InventoryMovementId == original.Id, cancellationToken);
            var isProductionReceipt = await dbContext.ProductionEvents.AsNoTracking()
                .AnyAsync(item => item.InventoryMovementId == original.Id, cancellationToken);
            if (isProductionReceipt)
            {
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed,
                    Errors: ["La Entrada pertenece a una orden de producción. Corrígela desde la orden para conservar su avance."]), cancellationToken);
            }
            if (isDocumentReceipt && normalized.Replacement is { } receiptReplacement &&
                (receiptReplacement.Type != InventoryMovementType.Entry || receiptReplacement.Purpose != InventoryMovementPurpose.DocumentReceipt))
            {
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed,
                    Errors: ["El reemplazo de una recepción documental debe conservar Entrada y propósito Recepción documental."]), cancellationToken);
            }
            var originalLineIds = original.Lines.Select(line => line.Id).ToArray();
            if (await dbContext.ProductionMaterialOperationLines.AsNoTracking()
                .AnyAsync(line => originalLineIds.Contains(line.InventoryMovementLineId), cancellationToken))
            {
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed,
                    Errors: ["El movimiento pertenece a una operación de material. Revierte la operación desde la orden de trabajo."]), cancellationToken);
            }
            var materialIssue = await dbContext.ProductionMaterialIssueLinks
                .Include(link => link.WorkOrder)
                .Include(link => link.OperationLines).ThenInclude(line => line.Operation)
                .Include(link => link.InventoryMovementLine)
                .Include(link => link.Lots)
                .Include(link => link.SupplyRequestLine).ThenInclude(line => line!.Reservations)
                .Include(link => link.SupplyRequestLine).ThenInclude(line => line!.IssueLinks).ThenInclude(issue => issue.InventoryMovementLine)
                .Include(link => link.SupplyRequestLine).ThenInclude(line => line!.SupplyRequest).ThenInclude(request => request.Events)
                .SingleOrDefaultAsync(link => link.InventoryMovementLineId.HasValue && originalLineIds.Contains(link.InventoryMovementLineId.Value), cancellationToken);
            if (materialIssue is not null)
            {
                var reversedMaterialOperations = await dbContext.ProductionMaterialOperations.AsNoTracking()
                    .Where(operation => operation.ReversesOperationId != null)
                    .Select(operation => operation.ReversesOperationId!.Value).ToListAsync(cancellationToken);
                if (materialIssue.OperationLines.Any(line =>
                        line.Operation.Type != ProductionMaterialOperationType.Reversal &&
                        !reversedMaterialOperations.Contains(line.Operation.Id)))
                    return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed,
                        Errors: ["El surtimiento ya tiene consumos o devoluciones. Revierte primero esas operaciones desde la orden."]), cancellationToken);
                if (normalized.Replacement is { } issueReplacement &&
                    (issueReplacement.Type != InventoryMovementType.Transfer ||
                     issueReplacement.Purpose != InventoryMovementPurpose.ProductionIssue ||
                     issueReplacement.Lines.Count != 1 ||
                     issueReplacement.Lines[0].ProductId != original.Lines.Single().ProductId ||
                     issueReplacement.Lines[0].DestinationLocationId != original.Lines.Single().DestinationLocationId))
                    return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed,
                        Errors: ["El reemplazo debe conservar producto, destino WIP y propósito del surtimiento vinculado."]), cancellationToken);
                if (normalized.Replacement is { } supplyReplacement && materialIssue.SupplyRequestLine is { } supplyLine)
                {
                    var deliveredByOthers = supplyLine.IssueLinks.Where(x => x.Id != materialIssue.Id)
                        .Sum(x => x.Quantity - x.CancelledQuantity);
                    if (supplyReplacement.Lines.Single().Quantity > supplyLine.RequiredQuantity -
                        supplyLine.CancelledQuantity + supplyLine.ReopenedQuantity - deliveredByOthers)
                        return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed,
                            Errors: ["El reemplazo supera el pendiente permitido por la solicitud de surtimiento."]), cancellationToken);
                }
            }
            if (await dbContext.WipDispositions.AnyAsync(disposition =>
                    originalLineIds.Contains(disposition.OriginalMovementLineId) &&
                    disposition.ReversesDispositionId == null &&
                    !dbContext.WipDispositions.Any(reversal => reversal.ReversesDispositionId == disposition.Id),
                    cancellationToken))
            {
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed,
                    Errors: ["Corrige primero las devoluciones WIP relacionadas con este movimiento."]), cancellationToken);
            }

            await LockOriginalAsync(original.Id, transaction, cancellationToken);
            var reversal = await reversalService.CreateAsync(original, authorizedBy.Id, fingerprint, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            InventoryMovementResult? replacementResult = null;
            if (normalized.Replacement is not null)
            {
                var replacementCommand = new InventoryMovementCommand(
                    Guid.NewGuid(),
                    normalized.Replacement.Type,
                    normalized.Pin,
                    normalized.Replacement.Lines,
                    normalized.Replacement.Reference,
                    normalized.Replacement.Notes,
                    normalized.Replacement.ApprovedSharedAssignments,
                    normalized.Replacement.Purpose,
                    normalized.Replacement.OperationalAreaId);
                replacementResult = materialIssue?.SupplyRequestLineId is Guid supplyLineId
                    ? await movementService.ConfirmAuthorizedAsync(replacementCommand, requestedBy,
                        productionSupplyLineId: supplyLineId, cancellationToken: cancellationToken)
                    : await movementService.ConfirmAsync(replacementCommand, cancellationToken);
                if (replacementResult.Status != InventoryMovementStatus.Success)
                    return await AbortAsync(transaction, Map(replacementResult), cancellationToken);
            }

            var correction = new InventoryMovementCorrection
            {
                OperationId = normalized.OperationId,
                RequestFingerprint = fingerprint,
                Type = normalized.Replacement is null
                    ? InventoryMovementCorrectionType.Reversal
                    : InventoryMovementCorrectionType.Replacement,
                OriginalMovementId = original.Id,
                ReversalMovementId = reversal.Id,
                ReplacementMovementId = replacementResult?.MovementId,
                Reason = normalized.Reason,
                RequestedByUserId = requestedBy.Id,
                AuthorizedByUserId = authorizedBy.Id,
                RecordedAt = timeProvider.GetUtcNow()
            };
            dbContext.InventoryMovementCorrections.Add(correction);
            if (materialIssue is not null)
            {
                var originalQuantity = materialIssue.Quantity;
                var replacementQuantity = normalized.Replacement?.Lines.Single().Quantity ?? 0;
                if (replacementResult?.MovementId is Guid replacementId)
                {
                    var replacementLine = await dbContext.InventoryMovementLines.Include(x => x.BalanceChanges)
                        .SingleAsync(line => line.MovementId == replacementId, cancellationToken);
                    materialIssue.InventoryMovementLineId = replacementLine.Id;
                    materialIssue.ProductId = replacementLine.ProductId;
                    materialIssue.WipLocationId = replacementLine.DestinationLocationId!.Value;
                    materialIssue.Quantity = replacementLine.Quantity;
                    dbContext.ProductionMaterialIssueLots.RemoveRange(materialIssue.Lots);
                    foreach (var change in replacementLine.BalanceChanges.Where(x => x.DeltaQuantity > 0 && x.LotId.HasValue))
                        materialIssue.Lots.Add(new ProductionMaterialIssueLot { LotId = change.LotId!.Value, Quantity = change.DeltaQuantity });
                }
                else
                {
                    dbContext.ProductionMaterialIssueLinks.Remove(materialIssue);
                }
                materialIssue.WorkOrder.Version++;
                if (materialIssue.SupplyRequestLine is { } supplyLine)
                {
                    var restore = Math.Max(0, originalQuantity - replacementQuantity);
                    foreach (var reservation in supplyLine.Reservations.Where(x => x.ReleasedQuantity > 0).OrderByDescending(x => x.CreatedAt))
                    {
                        var amount = Math.Min(restore, reservation.ReleasedQuantity);
                        reservation.ReleasedQuantity -= amount; restore -= amount; if (restore == 0) break;
                    }
                    var request = supplyLine.SupplyRequest;
                    request.Events.Add(new ProductionSupplyEvent { OperationId = normalized.OperationId,
                        RequestFingerprint = fingerprint, SupplyRequest = request, SupplyRequestLine = supplyLine,
                        Type = ProductionSupplyEventType.DeliveryReversed, ResponsibleUserId = authorizedBy.Id,
                        Quantity = originalQuantity - replacementQuantity, Reason = normalized.Reason,
                        RecordedAt = timeProvider.GetUtcNow() });
                    dbContext.Entry(request.Events.Last()).State = EntityState.Added;
                    request.Status = ProductionSupplyRequestStatus.InProgress;
                    request.Version++;
                }
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            if (isDocumentReceipt && receivingService is not null)
                await receivingService.RecalculateAfterCorrectionAsync(original.Id, normalized.OperationId, normalized.Reason, cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new(
                InventoryCorrectionStatus.Success,
                correction.Id,
                reversal.Id,
                replacementResult?.MovementId,
                Balances: replacementResult?.ResultingBalances ??
                    await reversalService.CurrentBalancesAsync(reversal, cancellationToken));
        }
        catch (PalletPlateException exception)
        {
            return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed, Errors: [exception.Message]), cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return await ExistingAsync(normalized.OperationId, fingerprint, cancellationToken)
                ?? new(InventoryCorrectionStatus.IdempotencyConflict);
        }
    }

    private async Task LockOriginalAsync(
        Guid id,
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is null)
            return;

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM inventory_movements WHERE id = {id} FOR UPDATE", cancellationToken);
    }

    private async Task<InventoryCorrectionResult?> ExistingAsync(
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var item = await dbContext.InventoryMovementCorrections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (item is null)
            return null;

        return item.RequestFingerprint == fingerprint
            ? new(InventoryCorrectionStatus.Success, item.Id, item.ReversalMovementId, item.ReplacementMovementId)
            : new(InventoryCorrectionStatus.IdempotencyConflict);
    }

    private static InventoryReplacementCommand? Normalize(InventoryReplacementCommand? value) => value is null ? null : value with
    {
        Reference = string.IsNullOrWhiteSpace(value.Reference) ? null : value.Reference.Trim(),
        Notes = string.IsNullOrWhiteSpace(value.Notes) ? null : value.Notes.Trim(),
        ApprovedSharedAssignments = (value.ApprovedSharedAssignments ?? [])
            .Distinct().OrderBy(item => item.ProductId).ThenBy(item => item.LocationId).ToArray()
    };

    private static InventoryCorrectionResult Map(InventoryMovementResult result) => result.Status switch
    {
        InventoryMovementStatus.InvalidPin => new(InventoryCorrectionStatus.InvalidPin),
        InventoryMovementStatus.RequiresLocationSharingConfirmation => new(
            InventoryCorrectionStatus.RequiresLocationSharingConfirmation,
            SharingConflicts: result.Conflicts),
        InventoryMovementStatus.BalanceChanged => new(
            InventoryCorrectionStatus.BalanceChanged,
            Errors: result.ValidationErrors),
        InventoryMovementStatus.IdempotencyConflict => new(InventoryCorrectionStatus.IdempotencyConflict),
        _ => new(InventoryCorrectionStatus.ValidationFailed, Errors: result.ValidationErrors)
    };

    private static string CreateFingerprint(InventoryCorrectionCommand command, Guid authorizedById)
    {
        var builder = new StringBuilder().Append(command.OriginalMovementId.ToString("N"))
            .Append('|').Append(command.RequestedByUserId.ToString("N"))
            .Append('|').Append(authorizedById.ToString("N"))
            .Append('|').Append(command.Reason);
        if (command.Replacement is { } replacement)
        {
            builder.Append('|').Append(replacement.Type).Append('|').Append(replacement.Reference)
                .Append('|').Append(replacement.Notes).Append('|').Append(replacement.Purpose)
                .Append('|').Append(replacement.OperationalAreaId);
            foreach (var line in replacement.Lines)
            {
                builder.Append('|').Append(line.ProductId.ToString("N"))
                    .Append(':').Append(line.Quantity.ToString("G29", CultureInfo.InvariantCulture))
                    .Append(':').Append(line.SourceLocationId)
                    .Append(':').Append(line.DestinationLocationId)
                    .Append(':').Append(line.LocationId)
                    .Append(':').Append(line.ExpectedBalanceVersion);
            }
        }

        return InventoryFingerprint.Hash(builder.ToString());
    }

    private async Task<InventoryCorrectionResult> AbortAsync(
        IDbContextTransaction? transaction,
        InventoryCorrectionResult result,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.RollbackAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        return result;
    }
}
