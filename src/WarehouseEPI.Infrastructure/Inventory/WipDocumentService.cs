using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record WipDocumentCommand(Guid OperationId, Guid ProductId, Guid WipLocationId,
    decimal Quantity, WipDocumentApplicationKind Kind, string Pin, Guid? DestinationLocationId = null,
    Guid? DocumentId = null, Guid? IssueLinkId = null, string? Reference = null, string? Notes = null,
    bool ApproveSharedDestination = false);

public sealed class WipDocumentService(WarehouseDbContext db, UserPinService pins, TimeProvider timeProvider)
{
    public async Task<InventoryMovementResult> ReverseAsync(Guid operationId, Guid originalOperationId, string pin, string reason, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(pin, token);
        if (user?.Role.Code != "ADMIN") return new(InventoryMovementStatus.InvalidPin);
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            return new(InventoryMovementStatus.ValidationFailed, Errors: ["Indica un motivo válido para el reverso."]);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var original = await db.WipDocumentApplications.Include(x => x.Document)
                .Include(x => x.InventoryMovementLine).ThenInclude(x => x!.Movement).ThenInclude(x => x.Lines).ThenInclude(x => x.BalanceChanges)
                .Where(x => x.OperationId == originalOperationId).ToListAsync(token);
            if (original.Count == 0 || original.Any(x => x.IssueLinkId.HasValue || x.Kind == WipDocumentApplicationKind.Reversal))
                return new(InventoryMovementStatus.ValidationFailed, Errors: ["La operación no existe o debe revertirse desde su orden de producción."]);
            if (tx is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({operationId.ToString()}, 0))", token);
                await InventoryMovementStore.LockLocationsAsync(original.Select(x => x.Document.WipLocationId)
                    .Concat(original.Where(x => x.InventoryMovementLine is not null).SelectMany(x => x.InventoryMovementLine!.BalanceChanges).Select(x => x.LocationId))
                    .Distinct().Order().ToArray(), tx, token);
            }
            var prior = await db.WipDocumentApplications.Where(x => x.OperationId == operationId).ToListAsync(token);
            if (prior.Count > 0) return prior.All(x => x.ResponsibleUserId == user.Id && x.Notes == reason.Trim() && original.Any(o => o.Id == x.ReversesApplicationId))
                ? new(InventoryMovementStatus.Success) : new(InventoryMovementStatus.IdempotencyConflict);
            var reversals = new Dictionary<Guid, InventoryMovement>();
            foreach (var movement in original.Where(x => x.InventoryMovementLine is not null).Select(x => x.InventoryMovementLine!.Movement).DistinctBy(x => x.Id))
            {
                var reversal = await new InventoryReversalService(db, timeProvider).CreateAsync(movement, user.Id, reason.Trim(), token);
                reversals.Add(movement.Id, reversal);
                db.InventoryMovementCorrections.Add(new InventoryMovementCorrection
                {
                    OperationId = Guid.NewGuid(),
                    RequestFingerprint = InventoryFingerprint.Hash(operationId + "|" + movement.Id),
                    Type = InventoryMovementCorrectionType.Reversal,
                    OriginalMovementId = movement.Id,
                    ReversalMovement = reversal,
                    Reason = reason.Trim(),
                    RequestedByUserId = user.Id,
                    AuthorizedByUserId = user.Id,
                    RecordedAt = timeProvider.GetUtcNow()
                });
            }
            var disposition = await db.WipDispositions.SingleOrDefaultAsync(x => x.OperationId == originalOperationId, token);
            if (disposition is not null)
                db.WipDispositions.Add(new WipDisposition
                {
                    OperationId = operationId,
                    RequestFingerprint = reason.Trim(),
                    OriginalMovementLineId = disposition.OriginalMovementLineId,
                    Type = disposition.Type,
                    Quantity = disposition.Quantity,
                    ResponsibleUserId = user.Id,
                    DestinationLocationId = disposition.DestinationLocationId,
                    InventoryMovement = disposition.InventoryMovementId is Guid movementId ? reversals.GetValueOrDefault(movementId) : null,
                    ReversesDispositionId = disposition.Id,
                    Reference = disposition.Reference,
                    Notes = reason.Trim(),
                    OccurredAt = timeProvider.GetUtcNow(),
                    RecordedAt = timeProvider.GetUtcNow()
                });
            await ReverseApplicationsAsync([originalOperationId], operationId, user, reason.Trim(), token);
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(InventoryMovementStatus.Success);
        }
        catch (PalletPlateException e)
        {
            if (tx is not null) await tx.RollbackAsync(token); db.ChangeTracker.Clear();
            return new(e.SharingConflicts.Count > 0 ? InventoryMovementStatus.RequiresLocationSharingConfirmation : InventoryMovementStatus.ValidationFailed,
                SharingConflicts: e.SharingConflicts, Errors: [e.Message]);
        }
    }

    internal static void RecordIssue(WarehouseDbContext db, InventoryMovement movement)
    {
        foreach (var line in movement.Lines)
            db.WipDocuments.Add(new WipDocument
            {
                MovementLine = line,
                ProductId = line.ProductId,
                WipLocationId = movement.OperationalAreaId!.Value,
                ResponsibleUserId = movement.ResponsibleUserId,
                OccurredAt = movement.OccurredAt,
                Quantity = line.Quantity,
                Lots = line.BalanceChanges.Where(x => x.DeltaQuantity < 0).GroupBy(x => x.LotId)
                    .Select(x => new WipDocumentLot { LotId = x.Key, Quantity = -x.Sum(y => y.DeltaQuantity) }).ToList()
            });
    }

    internal IQueryable<WipDocument> Documents => db.WipDocuments
        .Include(x => x.Lots).ThenInclude(x => x.Lot)
        .Include(x => x.Assignments).ThenInclude(x => x.IssueLink)
        .Include(x => x.Applications).Where(x => !x.IsCancelled);

    internal static IReadOnlyList<WipDocumentApplication> Effective(WipDocument document)
    {
        var reversed = document.Applications.Where(x => x.ReversesApplicationId.HasValue)
            .Select(x => x.ReversesApplicationId!.Value).ToHashSet();
        return document.Applications.Where(x => x.Kind != WipDocumentApplicationKind.Reversal && !reversed.Contains(x.Id)).ToArray();
    }

    internal static decimal Remaining(WipDocument document) => document.Quantity - Effective(document).Sum(x => x.Quantity);

    internal static decimal Available(WipDocument document, Guid? issueLinkId)
    {
        var applied = Effective(document);
        if (issueLinkId.HasValue)
            return document.Assignments.Where(x => x.IssueLinkId == issueLinkId).Sum(x => x.Quantity) -
                applied.Where(x => x.IssueLinkId == issueLinkId).Sum(x => x.Quantity);
        return Remaining(document) - document.Assignments.Sum(x => x.Quantity -
            applied.Where(a => a.IssueLinkId == x.IssueLinkId).Sum(a => a.Quantity));
    }

    public async Task<InventoryMovementResult> ConfirmAsync(WipDocumentCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user is null) return new(InventoryMovementStatus.InvalidPin);
        if (!RoleAccess.CanOperateWarehouse(user.Role.Code)) return new(InventoryMovementStatus.RoleNotAllowed, Errors: [RoleAccess.WarehouseWarning]);
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var applications = await ApplyAuthorizedAsync(command, user, token);
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(InventoryMovementStatus.Success, applications.Select(x => x.InventoryMovementLine?.MovementId).FirstOrDefault(x => x.HasValue),
                user.Id, user.FullName);
        }
        catch (PalletPlateException e)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(e.SharingConflicts.Count > 0 ? InventoryMovementStatus.RequiresLocationSharingConfirmation : InventoryMovementStatus.ValidationFailed,
                SharingConflicts: e.SharingConflicts, Errors: [e.Message]);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(InventoryMovementStatus.BalanceChanged, Errors: ["El registro cambió. Vuelve a consultar la entrega."]);
        }
    }

    internal async Task<IReadOnlyList<WipDocumentApplication>> ApplyAuthorizedAsync(WipDocumentCommand command, User user, CancellationToken token)
    {
        if (command.OperationId == Guid.Empty || command.Quantity <= 0 || command.Quantity > InventoryMovementRules.MaximumQuantity || decimal.Round(command.Quantity, 4) != command.Quantity ||
            command.Kind is not (WipDocumentApplicationKind.Consumption or WipDocumentApplicationKind.Scrap or WipDocumentApplicationKind.WarehouseReturn or WipDocumentApplicationKind.SupplierReturn))
            throw new PalletPlateException("La operación documental o su cantidad no es válida.");
        if (command.Reference?.Length > 120 || command.Notes?.Length > 500 ||
            (command.Kind == WipDocumentApplicationKind.SupplierReturn && string.IsNullOrWhiteSpace(command.Reference)) ||
            (command.Kind == WipDocumentApplicationKind.Scrap && string.IsNullOrWhiteSpace(command.Notes)))
            throw new PalletPlateException("Indica una referencia o motivo válido para la operación.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { Command = command with { Pin = "" }, UserId = user.Id }))));
        if (db.Database.CurrentTransaction is { } tx)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({command.OperationId.ToString()}, 0))", token);
            await InventoryMovementStore.LockLocationsAsync(new[] { command.WipLocationId }.Concat(command.DestinationLocationId is Guid destination ? [destination] : []).Order().ToArray(), tx, token);
        }
        var prior = await db.WipDocumentApplications.Include(x => x.InventoryMovementLine)
            .Where(x => x.OperationId == command.OperationId).ToListAsync(token);
        if (prior.Count > 0)
        {
            if (prior.Any(x => x.Fingerprint != fingerprint)) throw new PalletPlateException("La operación ya fue utilizada con otros datos.");
            return prior;
        }
        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.WipLocationId, token);
        var product = await db.Products.Include(x => x.BaseUnit).SingleOrDefaultAsync(x => x.Id == command.ProductId, token);
        if (location is null || !location.IsWip || !location.IsOperational || product is null || !product.IsActive || !product.BaseUnit.IsActive ||
            (!product.BaseUnit.AllowsDecimals && decimal.Truncate(command.Quantity) != command.Quantity))
            throw new PalletPlateException("El producto, la unidad o el destino WIP no están disponibles.");
        if (await db.InventoryBalances.AnyAsync(x => x.LocationId == command.WipLocationId && x.Quantity != 0, token))
            throw new PalletPlateException("Completa el corte documental ADMIN antes de procesar este WIP.");
        var documents = await Documents.Where(x => x.ProductId == command.ProductId && x.WipLocationId == command.WipLocationId &&
            (!command.DocumentId.HasValue || x.Id == command.DocumentId)).OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(token);
        if (documents.Sum(x => Available(x, command.IssueLinkId)) < command.Quantity)
            throw new PalletPlateException("La cantidad supera lo pendiente documental. Revisa las entregas y sus asignaciones a órdenes.");
        var remaining = command.Quantity;
        var result = new List<WipDocumentApplication>();
        foreach (var document in documents)
        {
            var takeDocument = Math.Min(remaining, Math.Max(0, Available(document, command.IssueLinkId)));
            var applied = Effective(document);
            foreach (var lot in document.Lots.OrderBy(x => x.Lot?.LotDate).ThenBy(x => x.Id))
            {
                var take = Math.Min(takeDocument, Math.Max(0, lot.Quantity - applied.Where(x => x.LotId == lot.LotId).Sum(x => x.Quantity)));
                if (take <= 0) continue;
                var application = new WipDocumentApplication
                {
                    OperationId = command.OperationId,
                    Fingerprint = fingerprint,
                    Document = document,
                    IssueLinkId = command.IssueLinkId,
                    LotId = lot.LotId,
                    Quantity = take,
                    Kind = command.Kind,
                    ResponsibleUserId = user.Id,
                    RecordedAt = timeProvider.GetUtcNow(),
                    Reference = command.Reference?.Trim(),
                    Notes = command.Notes?.Trim()
                };
                result.Add(application);
                remaining -= take; takeDocument -= take;
                if (takeDocument == 0) break;
            }
            if (remaining == 0) break;
        }
        if (remaining != 0) throw new PalletPlateException("Los lotes documentales no concuerdan con lo pendiente. Revisa la entrega.");
        if (command.Kind == WipDocumentApplicationKind.WarehouseReturn)
        {
            if (command.DestinationLocationId is not Guid destinationId) throw new PalletPlateException("Selecciona una ubicación de almacén para el regreso.");
            var entry = new InventoryMovementCommand(command.OperationId, InventoryMovementType.Entry, "",
                result.Select(x => new InventoryMovementLineCommand(command.ProductId, x.Quantity,
                    DestinationLocationId: destinationId, DestinationLotId: x.LotId)).ToArray(), command.Reference, command.Notes,
                command.ApproveSharedDestination ? [new(command.ProductId, destinationId)] : [], InventoryMovementPurpose.WipWarehouseReturn);
            var entryResult = await new InventoryMovementService(db, pins, timeProvider).ConfirmAuthorizedAsync(entry, user, cancellationToken: token);
            if (entryResult.Status != InventoryMovementStatus.Success)
                throw new PalletPlateException(entryResult.Conflicts.Count > 0 ? "El destino está compartido. Confirma la ubicación compartida antes de devolver." : entryResult.ValidationErrors.FirstOrDefault() ?? "No fue posible registrar la entrada.", entryResult.Conflicts);
            var lines = await db.InventoryMovementLines.Where(x => x.MovementId == entryResult.MovementId).OrderBy(x => x.LineNumber).ToListAsync(token);
            foreach (var pair in result.Zip(lines)) pair.First.InventoryMovementLine = pair.Second;
        }
        db.WipDocumentApplications.AddRange(result);
        return result;
    }

    internal async Task AssignAsync(ProductionMaterialIssueLink issue, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is { } tx)
            await InventoryMovementStore.LockLocationsAsync([issue.WipLocationId], tx, token);
        var documents = await Documents.Where(x => x.ProductId == issue.ProductId && x.WipLocationId == issue.WipLocationId &&
            (!issue.InventoryMovementLineId.HasValue || x.MovementLineId == issue.InventoryMovementLineId))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(token);
        var remaining = issue.Quantity - issue.CancelledQuantity;
        foreach (var document in documents)
        {
            var take = Math.Min(remaining, Math.Max(0, Available(document, null)));
            if (take > 0) db.WipDocumentAssignments.Add(new() { Document = document, IssueLink = issue, Quantity = take });
            remaining -= take;
            if (remaining == 0) break;
        }
        if (remaining != 0) throw new PalletPlateException("Las entregas documentales libres no cubren la asignación a la orden.");
    }

    internal async Task ReleaseAssignmentAsync(Guid issueLinkId, decimal quantity, CancellationToken token)
    {
        var assignments = await db.WipDocumentAssignments.Include(x => x.Document).ThenInclude(x => x.Applications)
            .Where(x => x.IssueLinkId == issueLinkId).OrderByDescending(x => x.Id).ToListAsync(token);
        foreach (var assignment in assignments)
        {
            var used = Effective(assignment.Document).Where(x => x.IssueLinkId == issueLinkId).Sum(x => x.Quantity);
            var release = Math.Min(quantity, Math.Max(0, assignment.Quantity - used));
            assignment.Quantity -= release; quantity -= release;
            if (quantity == 0) break;
        }
        if (quantity != 0) throw new PalletPlateException("La cantidad documental sin aplicar cambió. Revisa la asignación.");
    }

    internal async Task ReverseApplicationsAsync(IReadOnlyCollection<Guid> operationIds, Guid reversalOperationId, User user, string reason, CancellationToken token)
    {
        var applications = await db.WipDocumentApplications.Include(x => x.Document).Include(x => x.InventoryMovementLine)
            .Where(x => operationIds.Contains(x.OperationId) && x.Kind != WipDocumentApplicationKind.Reversal).ToListAsync(token);
        if (db.Database.CurrentTransaction is { } tx)
            await InventoryMovementStore.LockLocationsAsync(applications.Select(x => x.Document.WipLocationId).Distinct().Order().ToArray(), tx, token);
        foreach (var application in applications)
        {
            if (await db.WipDocumentApplications.AnyAsync(x => x.ReversesApplicationId == application.Id, token))
                throw new PalletPlateException("La aplicación documental ya fue revertida.");
            db.WipDocumentApplications.Add(new WipDocumentApplication
            {
                OperationId = reversalOperationId,
                Fingerprint = reason,
                DocumentId = application.DocumentId,
                IssueLinkId = application.IssueLinkId,
                LotId = application.LotId,
                Kind = WipDocumentApplicationKind.Reversal,
                ReversesApplicationId = application.Id,
                Quantity = application.Quantity,
                ResponsibleUserId = user.Id,
                InventoryMovementLine = application.InventoryMovementLine is { } originalLine
                    ? db.InventoryMovementCorrections.Local.FirstOrDefault(c => c.OriginalMovementId == originalLine.MovementId)?.ReversalMovement.Lines.SingleOrDefault(l => l.LineNumber == originalLine.LineNumber) : null,
                RecordedAt = timeProvider.GetUtcNow(),
                Notes = reason
            });
        }
    }
}
