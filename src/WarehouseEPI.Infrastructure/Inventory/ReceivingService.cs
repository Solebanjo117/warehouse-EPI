using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed class ReceivingService(
    WarehouseDbContext db,
    UserPinService pins,
    InventoryMovementService movements,
    TimeProvider timeProvider)
{
    public async Task<ReceivingCommandResult> OpenAsync(OpenReceivingDocumentCommand command, CancellationToken token = default)
    {
        var normalized = Normalize(command);
        var fingerprint = Fingerprint(normalized);
        var existing = await db.ReceivingDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == command.OperationId, token);
        if (existing is not null)
            return existing.RequestFingerprint == fingerprint
                ? new(ReceivingCommandStatus.Success, existing.Id, DocumentStatus: existing.Status)
                : new(ReceivingCommandStatus.IdempotencyConflict);

        var user = await AuthenticateAsync(command.Pin, token);
        if (user is null) return new(ReceivingCommandStatus.InvalidPin);
        var errors = ValidateOpen(normalized);
        var productIds = normalized.Lines.Select(item => item.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Include(item => item.BaseUnit)
            .Where(item => productIds.Contains(item.Id) && item.IsActive && item.BaseUnit.IsActive)
            .ToDictionaryAsync(item => item.Id, token);
        foreach (var id in productIds.Where(id => !products.ContainsKey(id))) errors.Add($"El producto {id} no existe o está inactivo.");
        if (errors.Count > 0) return new(ReceivingCommandStatus.ValidationFailed, Errors: errors);

        var duplicate = await db.ReceivingDocuments.AsNoTracking().AnyAsync(item => item.Type == normalized.Type &&
            item.NormalizedNumber == normalized.Number.ToUpperInvariant() && item.NormalizedOrigin == normalized.Origin.ToUpperInvariant() &&
            item.Status != ReceivingDocumentStatus.Cancelled, token);
        if (duplicate) return new(ReceivingCommandStatus.ValidationFailed, Errors: ["Ya existe un documento no cancelado con el mismo tipo, número y origen."]);

        var now = timeProvider.GetUtcNow();
        var document = new ReceivingDocument
        {
            OperationId = normalized.OperationId,
            RequestFingerprint = fingerprint,
            Type = normalized.Type,
            Number = normalized.Number,
            NormalizedNumber = normalized.Number.ToUpperInvariant(),
            Origin = normalized.Origin,
            NormalizedOrigin = normalized.Origin.ToUpperInvariant(),
            DocumentDate = normalized.DocumentDate,
            Notes = normalized.Notes,
            OpenedByUserId = user.Id,
            OpenedAt = now
        };
        foreach (var (line, index) in normalized.Lines.OrderBy(item => products[item.ProductId].Sku).Select((item, index) => (item, index)))
            document.Lines.Add(new ReceivingDocumentLine { LineNumber = index + 1, ProductId = line.ProductId, UnitId = products[line.ProductId].BaseUnitId, ExpectedQuantity = line.ExpectedQuantity });
        document.Events.Add(new ReceivingDocumentEvent { OperationId = command.OperationId, RequestFingerprint = fingerprint, Type = ReceivingDocumentEventType.Opened, ActorUserId = user.Id, RecordedAt = now, Notes = "Documento abierto con cantidades esperadas congeladas." });
        db.ReceivingDocuments.Add(document);
        try
        {
            await db.SaveChangesAsync(token);
            return new(ReceivingCommandStatus.Success, document.Id, DocumentStatus: document.Status);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.ReceivingDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == command.OperationId, token);
            return existing?.RequestFingerprint == fingerprint
                ? new(ReceivingCommandStatus.Success, existing.Id, DocumentStatus: existing.Status)
                : new(ReceivingCommandStatus.IdempotencyConflict, Errors: ["El documento coincide con una operación o identidad ya utilizada."]);
        }
    }

    public async Task<ReceivingCommandResult> ConfirmAsync(ConfirmReceivingCommand command, CancellationToken token = default)
    {
        var normalized = Normalize(command);
        var fingerprint = Fingerprint(normalized);
        var previous = await db.ReceivingConfirmations.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == command.OperationId, token);
        if (previous is not null)
            return previous.RequestFingerprint == fingerprint
                ? new(ReceivingCommandStatus.Success, previous.ReceivingDocumentId, previous.InventoryMovementId)
                : new(ReceivingCommandStatus.IdempotencyConflict);
        var user = await AuthenticateAsync(command.Pin, token);
        if (user is null) return new(ReceivingCommandStatus.InvalidPin);
        var errors = ValidateConfirmation(normalized);
        if (errors.Count > 0) return new(ReceivingCommandStatus.ValidationFailed, Errors: errors);

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var document = await db.ReceivingDocuments.Include(item => item.Lines)
                .SingleOrDefaultAsync(item => item.Id == normalized.DocumentId, token);
            if (document is null) return await AbortAsync(transaction, new(ReceivingCommandStatus.NotFound), token);
            if (document.Status is not (ReceivingDocumentStatus.Open or ReceivingDocumentStatus.PartiallyReceived))
                return await AbortAsync(transaction, new(ReceivingCommandStatus.InvalidState, document.Id, DocumentStatus: document.Status, Errors: ["El documento ya no admite recepciones."]), token);
            if (transaction is not null)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM receiving_documents WHERE id = {document.Id} FOR UPDATE", token);

            var current = await EffectiveQuantitiesAsync(document.Id, token);
            var projected = current.ToDictionary(item => item.Key, item => item.Value);
            foreach (var line in normalized.Lines)
                projected[line.ProductId] = projected.GetValueOrDefault(line.ProductId) + line.Quantity;
            var expected = document.Lines.ToDictionary(item => item.ProductId, item => item.ExpectedQuantity);
            var hasUnexpectedOrOverage = projected.Any(item => !expected.TryGetValue(item.Key, out var quantity) || item.Value > quantity);
            if (hasUnexpectedOrOverage && (!normalized.DifferenceAcknowledged || string.IsNullOrWhiteSpace(normalized.DifferenceNotes)))
                return await AbortAsync(transaction, new(ReceivingCommandStatus.RequiresDifferenceAcknowledgement, document.Id, Errors: ["Confirma los sobrantes o productos inesperados y escribe una nota."]), token);

            var movementCommand = new InventoryMovementCommand(
                normalized.OperationId,
                InventoryMovementType.Entry,
                normalized.Pin,
                normalized.Lines.Select(item => new InventoryMovementLineCommand(item.ProductId, item.Quantity, DestinationLocationId: item.DestinationLocationId)).ToArray(),
                Reference: document.Number,
                Notes: normalized.DifferenceNotes,
                ApprovedSharedAssignments: normalized.ApprovedSharedAssignments,
                Purpose: InventoryMovementPurpose.DocumentReceipt);
            var movementResult = await movements.ConfirmAuthorizedAsync(movementCommand, user, token);
            if (movementResult.Status != InventoryMovementStatus.Success || movementResult.MovementId is not Guid movementId)
                return await AbortAsync(transaction, MapMovementResult(movementResult, document.Id), token);

            var movementLines = await db.InventoryMovementLines.Where(item => item.MovementId == movementId).OrderBy(item => item.LineNumber).ToListAsync(token);
            var now = timeProvider.GetUtcNow();
            var confirmation = new ReceivingConfirmation
            {
                OperationId = normalized.OperationId,
                RequestFingerprint = fingerprint,
                ReceivingDocumentId = document.Id,
                InventoryMovementId = movementId,
                ResponsibleUserId = user.Id,
                DifferenceAcknowledged = normalized.DifferenceAcknowledged,
                DifferenceNotes = normalized.DifferenceNotes,
                OccurredAt = now,
                RecordedAt = now
            };
            for (var index = 0; index < movementLines.Count; index++)
            {
                var input = normalized.Lines[index];
                confirmation.Lines.Add(new ReceivingConfirmationLine
                {
                    ReceivingDocumentLineId = document.Lines.SingleOrDefault(item => item.ProductId == input.ProductId)?.Id,
                    InventoryMovementLineId = movementLines[index].Id,
                    ExternalLotReference = input.ExternalLotReference
                });
            }
            db.ReceivingConfirmations.Add(confirmation);
            document.Events.Add(new ReceivingDocumentEvent { OperationId = normalized.OperationId, RequestFingerprint = fingerprint, Type = ReceivingDocumentEventType.ReceiptConfirmed, ActorUserId = user.Id, RecordedAt = now, Notes = normalized.DifferenceNotes });
            await db.SaveChangesAsync(token);
            await ApplyStatusAsync(document, correction: false, token);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ReceivingCommandStatus.Success, document.Id, movementId, document.Status);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await AbortAsync(transaction, new(ReceivingCommandStatus.ConcurrencyConflict, command.DocumentId, Errors: ["El documento cambió mientras se confirmaba. Recárgalo e inténtalo nuevamente."]), token);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            previous = await db.ReceivingConfirmations.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == command.OperationId, token);
            return previous?.RequestFingerprint == fingerprint
                ? new(ReceivingCommandStatus.Success, previous.ReceivingDocumentId, previous.InventoryMovementId)
                : new(ReceivingCommandStatus.IdempotencyConflict);
        }
    }

    public Task<ReceivingCommandResult> CloseAsync(CompleteReceivingDocumentCommand command, CancellationToken token = default) => CompleteAsync(command, cancel: false, token);
    public Task<ReceivingCommandResult> CancelAsync(CompleteReceivingDocumentCommand command, CancellationToken token = default) => CompleteAsync(command, cancel: true, token);

    public async Task RecalculateAfterCorrectionAsync(Guid originalMovementId, Guid correctionOperationId, string reason, CancellationToken token = default)
    {
        var document = await db.ReceivingConfirmations.Where(item => item.InventoryMovementId == originalMovementId)
            .Select(item => item.ReceivingDocument).SingleOrDefaultAsync(token);
        if (document is null || document.Status is ReceivingDocumentStatus.ClosedWithDifferences or ReceivingDocumentStatus.Cancelled) return;
        var now = timeProvider.GetUtcNow();
        document.Events.Add(new ReceivingDocumentEvent { Type = ReceivingDocumentEventType.ReceiptCorrected, RecordedAt = now, Notes = reason });
        await ApplyStatusAsync(document, correction: true, token);
        await db.SaveChangesAsync(token);
    }

    private async Task<ReceivingCommandResult> CompleteAsync(CompleteReceivingDocumentCommand command, bool cancel, CancellationToken token)
    {
        var reason = command.Reason?.Trim() ?? string.Empty;
        var fingerprint = Hash($"{command.DocumentId:N}|{cancel}|{reason}");
        var existing = await db.ReceivingDocumentEvents.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == command.OperationId, token);
        if (existing is not null)
            return existing.RequestFingerprint == fingerprint ? new(ReceivingCommandStatus.Success, existing.ReceivingDocumentId) : new(ReceivingCommandStatus.IdempotencyConflict);
        var user = await AuthenticateAsync(command.Pin, token);
        if (user is null) return new(ReceivingCommandStatus.InvalidPin);
        if (command.OperationId == Guid.Empty || reason.Length is 0 or > 500)
            return new(ReceivingCommandStatus.ValidationFailed, Errors: ["La operación y un motivo de hasta 500 caracteres son obligatorios."]);

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var document = await db.ReceivingDocuments.Include(item => item.Confirmations).SingleOrDefaultAsync(item => item.Id == command.DocumentId, token);
            if (document is null) return await AbortAsync(transaction, new(ReceivingCommandStatus.NotFound), token);
            if (document.Status is not (ReceivingDocumentStatus.Open or ReceivingDocumentStatus.PartiallyReceived))
                return await AbortAsync(transaction, new(ReceivingCommandStatus.InvalidState, document.Id, DocumentStatus: document.Status), token);
            if (cancel && document.Confirmations.Count != 0)
                return await AbortAsync(transaction, new(ReceivingCommandStatus.InvalidState, document.Id, Errors: ["Un documento con recepciones no puede cancelarse; ciérralo con diferencias."]), token);
            if (!cancel && document.Confirmations.Count == 0)
                return await AbortAsync(transaction, new(ReceivingCommandStatus.InvalidState, document.Id, Errors: ["Recibe al menos una línea antes de cerrar con diferencias."]), token);
            var now = timeProvider.GetUtcNow();
            if (cancel)
            {
                document.Status = ReceivingDocumentStatus.Cancelled;
                document.CancelledAt = now;
                document.CancelledByUserId = user.Id;
                document.CancelReason = reason;
            }
            else
            {
                document.Status = ReceivingDocumentStatus.ClosedWithDifferences;
                document.ClosedAt = now;
                document.ClosedByUserId = user.Id;
                document.CloseReason = reason;
            }
            document.Events.Add(new ReceivingDocumentEvent { OperationId = command.OperationId, RequestFingerprint = fingerprint, Type = cancel ? ReceivingDocumentEventType.Cancelled : ReceivingDocumentEventType.ClosedWithDifferences, ActorUserId = user.Id, Notes = reason, RecordedAt = now });
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ReceivingCommandStatus.Success, document.Id, DocumentStatus: document.Status);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await AbortAsync(transaction, new(ReceivingCommandStatus.ConcurrencyConflict, command.DocumentId), token);
        }
    }

    private async Task ApplyStatusAsync(ReceivingDocument document, bool correction, CancellationToken token)
    {
        var quantities = await EffectiveQuantitiesAsync(document.Id, token);
        var expected = await db.ReceivingDocumentLines.AsNoTracking().Where(item => item.ReceivingDocumentId == document.Id)
            .ToDictionaryAsync(item => item.ProductId, item => item.ExpectedQuantity, token);
        var any = quantities.Values.Any(value => value != 0);
        var exact = expected.All(item => quantities.GetValueOrDefault(item.Key) == item.Value) && quantities.Keys.All(expected.ContainsKey);
        var previous = document.Status;
        var now = timeProvider.GetUtcNow();
        document.Status = exact ? ReceivingDocumentStatus.Completed : any ? ReceivingDocumentStatus.PartiallyReceived : ReceivingDocumentStatus.Open;
        document.CompletedAt = exact ? now : null;
        if (document.Status == previous) return;
        if (exact)
            document.Events.Add(new ReceivingDocumentEvent { Type = ReceivingDocumentEventType.AutomaticallyCompleted, RecordedAt = now, Notes = "Las cantidades efectivas coinciden exactamente con el documento." });
        else if (correction && previous == ReceivingDocumentStatus.Completed)
            document.Events.Add(new ReceivingDocumentEvent { Type = ReceivingDocumentEventType.ReopenedAfterCorrection, RecordedAt = now, Notes = "Una corrección dejó cantidades pendientes." });
    }

    private async Task<Dictionary<Guid, decimal>> EffectiveQuantitiesAsync(Guid documentId, CancellationToken token)
    {
        var originalIds = await db.ReceivingConfirmations.AsNoTracking().Where(item => item.ReceivingDocumentId == documentId).Select(item => item.InventoryMovementId).ToArrayAsync(token);
        if (originalIds.Length == 0) return [];
        var corrections = await db.InventoryMovementCorrections.AsNoTracking().Where(item => originalIds.Contains(item.OriginalMovementId)).ToListAsync(token);
        var corrected = corrections.Select(item => item.OriginalMovementId).ToHashSet();
        var effectiveIds = originalIds.Where(id => !corrected.Contains(id)).Concat(corrections.Where(item => item.ReplacementMovementId != null).Select(item => item.ReplacementMovementId!.Value)).ToArray();
        return await db.InventoryMovementLines.AsNoTracking().Where(item => effectiveIds.Contains(item.MovementId))
            .GroupBy(item => item.ProductId).Select(group => new { ProductId = group.Key, Quantity = group.Sum(item => item.Quantity) })
            .ToDictionaryAsync(item => item.ProductId, item => item.Quantity, token);
    }

    private async Task<User?> AuthenticateAsync(string pin, CancellationToken token)
    {
        var user = await pins.AuthenticateAsync(pin, token);
        return user?.Role.Code is "ADMIN" or "OPERATOR" ? user : null;
    }

    private static OpenReceivingDocumentCommand Normalize(OpenReceivingDocumentCommand command) => command with
    {
        Number = command.Number?.Trim() ?? string.Empty,
        Origin = command.Origin?.Trim() ?? string.Empty,
        Notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
        Lines = command.Lines.Where(item => item.ProductId != Guid.Empty || item.ExpectedQuantity != 0).ToArray()
    };

    private static ConfirmReceivingCommand Normalize(ConfirmReceivingCommand command) => command with
    {
        DifferenceNotes = string.IsNullOrWhiteSpace(command.DifferenceNotes) ? null : command.DifferenceNotes.Trim(),
        Lines = command.Lines.Where(item => item.ProductId != Guid.Empty || item.Quantity != 0 || item.DestinationLocationId != Guid.Empty)
            .Select(item => item with { ExternalLotReference = string.IsNullOrWhiteSpace(item.ExternalLotReference) ? null : item.ExternalLotReference.Trim() }).ToArray(),
        ApprovedSharedAssignments = (command.ApprovedSharedAssignments ?? []).Distinct().OrderBy(item => item.ProductId).ThenBy(item => item.LocationId).ToArray()
    };

    private static List<string> ValidateOpen(OpenReceivingDocumentCommand command)
    {
        var errors = new List<string>();
        if (command.OperationId == Guid.Empty) errors.Add("La operación no es válida.");
        if (command.Number.Length is 0 or > 120) errors.Add("El número documental es obligatorio y admite hasta 120 caracteres.");
        if (command.Origin.Length is 0 or > 160) errors.Add("El origen es obligatorio y admite hasta 160 caracteres.");
        if (command.Notes?.Length > 500) errors.Add("Las notas admiten hasta 500 caracteres.");
        if (command.Lines.Count == 0) errors.Add("Agrega al menos un producto esperado.");
        if (command.Lines.GroupBy(item => item.ProductId).Any(group => group.Count() > 1)) errors.Add("Cada producto puede aparecer una sola vez en el documento.");
        if (command.Lines.Any(item => item.ProductId == Guid.Empty || item.ExpectedQuantity <= 0 || decimal.Round(item.ExpectedQuantity, 4) != item.ExpectedQuantity)) errors.Add("Cada línea requiere producto y cantidad positiva con máximo cuatro decimales.");
        return errors;
    }

    private static List<string> ValidateConfirmation(ConfirmReceivingCommand command)
    {
        var errors = new List<string>();
        if (command.OperationId == Guid.Empty || command.DocumentId == Guid.Empty) errors.Add("La operación y el documento son obligatorios.");
        if (command.Lines.Count == 0) errors.Add("Agrega al menos una línea recibida.");
        if (command.DifferenceNotes?.Length > 500) errors.Add("La nota de diferencias admite hasta 500 caracteres.");
        if (command.Lines.Any(item => item.ProductId == Guid.Empty || item.DestinationLocationId == Guid.Empty || item.Quantity <= 0 || decimal.Round(item.Quantity, 4) != item.Quantity || item.ExternalLotReference?.Length > 120)) errors.Add("Cada línea requiere producto, destino y cantidad positiva; el lote externo admite hasta 120 caracteres.");
        return errors;
    }

    private static string Fingerprint(OpenReceivingDocumentCommand command)
    {
        var value = new StringBuilder().Append(command.Type).Append('|').Append(command.Number.ToUpperInvariant()).Append('|').Append(command.Origin.ToUpperInvariant()).Append('|').Append(command.DocumentDate).Append('|').Append(command.Notes);
        foreach (var line in command.Lines.OrderBy(item => item.ProductId)) value.Append('|').Append(line.ProductId.ToString("N")).Append(':').Append(line.ExpectedQuantity.ToString("G29", CultureInfo.InvariantCulture));
        return Hash(value.ToString());
    }

    private static string Fingerprint(ConfirmReceivingCommand command)
    {
        var value = new StringBuilder().Append(command.DocumentId.ToString("N")).Append('|').Append(command.DifferenceAcknowledged).Append('|').Append(command.DifferenceNotes);
        foreach (var line in command.Lines) value.Append('|').Append(line.ProductId.ToString("N")).Append(':').Append(line.Quantity.ToString("G29", CultureInfo.InvariantCulture)).Append(':').Append(line.DestinationLocationId.ToString("N")).Append(':').Append(line.ExternalLotReference);
        foreach (var approval in command.ApprovedSharedAssignments ?? []) value.Append("|A:").Append(approval.ProductId.ToString("N")).Append(':').Append(approval.LocationId.ToString("N"));
        return Hash(value.ToString());
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static ReceivingCommandResult MapMovementResult(InventoryMovementResult result, Guid documentId) => result.Status switch
    {
        InventoryMovementStatus.InvalidPin => new(ReceivingCommandStatus.InvalidPin, documentId),
        InventoryMovementStatus.RequiresLocationSharingConfirmation => new(ReceivingCommandStatus.RequiresLocationSharingConfirmation, documentId, SharingConflicts: result.Conflicts),
        InventoryMovementStatus.BalanceChanged => new(ReceivingCommandStatus.BalanceChanged, documentId, Errors: result.ValidationErrors),
        InventoryMovementStatus.IdempotencyConflict => new(ReceivingCommandStatus.IdempotencyConflict, documentId),
        _ => new(ReceivingCommandStatus.ValidationFailed, documentId, Errors: result.ValidationErrors)
    };

    private async Task<ReceivingCommandResult> AbortAsync(IDbContextTransaction? transaction, ReceivingCommandResult result, CancellationToken token)
    {
        if (transaction is not null) await transaction.RollbackAsync(token);
        db.ChangeTracker.Clear();
        return result;
    }
}
