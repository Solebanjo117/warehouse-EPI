using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed class InventoryCorrectionService(
    WarehouseDbContext dbContext,
    UserPinService userPinService,
    InventoryMovementService movementService,
    TimeProvider timeProvider)
{
    public async Task<InventoryCorrectionResult> ConfirmAsync(
        InventoryCorrectionCommand command,
        CancellationToken cancellationToken = default)
    {
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (command.OperationId == Guid.Empty || command.OriginalMovementId == Guid.Empty ||
            command.RequestedByUserId == Guid.Empty || reason.Length is 0 or > 500)
            return new(InventoryCorrectionStatus.ValidationFailed, Errors: ["La operación, el movimiento original y el motivo son obligatorios; el motivo admite hasta 500 caracteres."]);

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
        if (existing is not null) return existing;

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var original = await dbContext.InventoryMovements
                .Include(movement => movement.Lines).ThenInclude(line => line.BalanceChanges)
                .SingleOrDefaultAsync(movement => movement.Id == normalized.OriginalMovementId, cancellationToken);
            if (original is null)
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.ValidationFailed, Errors: ["El movimiento original no existe."]), cancellationToken);
            if (await dbContext.InventoryMovementCorrections.AnyAsync(item => item.OriginalMovementId == original.Id, cancellationToken))
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.AlreadyCorrected), cancellationToken);
            if (await dbContext.InventoryMovementCorrections.AnyAsync(item => item.ReversalMovementId == original.Id, cancellationToken))
                return await AbortAsync(transaction, new(InventoryCorrectionStatus.CannotCorrectReversal), cancellationToken);

            await LockOriginalAsync(original.Id, transaction, cancellationToken);
            var reversal = await CreateReversalAsync(original, authorizedBy.Id, fingerprint, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            InventoryMovementResult? replacementResult = null;
            if (normalized.Replacement is not null)
            {
                replacementResult = await movementService.ConfirmAsync(new(
                    Guid.NewGuid(), normalized.Replacement.Type, normalized.Pin,
                    normalized.Replacement.Lines, normalized.Replacement.Reference,
                    normalized.Replacement.Notes, normalized.Replacement.ApprovedSharedAssignments), cancellationToken);
                if (replacementResult.Status != InventoryMovementStatus.Success)
                    return await AbortAsync(transaction, Map(replacementResult), cancellationToken);
            }

            var correction = new InventoryMovementCorrection
            {
                OperationId = normalized.OperationId,
                RequestFingerprint = fingerprint,
                Type = normalized.Replacement is null ? InventoryMovementCorrectionType.Reversal : InventoryMovementCorrectionType.Replacement,
                OriginalMovementId = original.Id,
                ReversalMovementId = reversal.Id,
                ReplacementMovementId = replacementResult?.MovementId,
                Reason = normalized.Reason,
                RequestedByUserId = requestedBy.Id,
                AuthorizedByUserId = authorizedBy.Id,
                RecordedAt = timeProvider.GetUtcNow()
            };
            dbContext.InventoryMovementCorrections.Add(correction);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return new(InventoryCorrectionStatus.Success, correction.Id, reversal.Id, replacementResult?.MovementId,
                Balances: replacementResult?.ResultingBalances ?? await CurrentBalancesAsync(reversal, cancellationToken));
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return await ExistingAsync(normalized.OperationId, fingerprint, cancellationToken) ?? new(InventoryCorrectionStatus.IdempotencyConflict);
        }
    }

    private async Task<InventoryMovement> CreateReversalAsync(InventoryMovement original, Guid authorizedById, string correctionFingerprint, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var legacyProducts = original.Lines.Where(line => line.BalanceChanges.Any(change => change.LotId is null)).Select(line => line.ProductId).Distinct().ToArray();
        var existingLegacyLots = await dbContext.ProductLots.Where(lot => legacyProducts.Contains(lot.ProductId)).ToListAsync(cancellationToken);
        var legacyLots = existingLegacyLots.GroupBy(lot => lot.ProductId)
            .ToDictionary(group => group.Key, group => group.OrderBy(lot => lot.CreatedAt).ThenBy(lot => lot.NormalizedNumber).First());
        foreach (var productId in legacyProducts.Where(id => !legacyLots.ContainsKey(id)))
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("America/Matamoros")).DateTime);
            var number = $"AUTO-{date:yyyyMMdd}";
            var lot = new ProductLot { ProductId = productId, Number = number, NormalizedNumber = number, LotDate = date, CreatedAt = now };
            dbContext.ProductLots.Add(lot); legacyLots.Add(productId, lot);
        }
        var keys = original.Lines.SelectMany(line => line.BalanceChanges.Select(change => new BalanceKey(line.ProductId, change.LocationId, change.LotId ?? legacyLots[line.ProductId].Id))).Distinct().ToArray();
        var productIds = keys.Select(key => key.ProductId).Distinct().ToArray();
        var locationIds = keys.Select(key => key.LocationId).Distinct().ToArray();
        var balances = await dbContext.InventoryBalances.Where(balance => productIds.Contains(balance.ProductId) && locationIds.Contains(balance.LocationId))
            .ToDictionaryAsync(balance => new BalanceKey(balance.ProductId, balance.LocationId, balance.LotId), cancellationToken);
        foreach (var key in keys.Where(key => !balances.ContainsKey(key)))
        {
            var balance = new InventoryBalance { ProductId = key.ProductId, LocationId = key.LocationId, LotId = key.LotId, Quantity = 0m, UpdatedAt = timeProvider.GetUtcNow() };
            dbContext.InventoryBalances.Add(balance); balances.Add(key, balance);
        }
        var reversal = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = Hash(correctionFingerprint + "|reversal"),
            Type = ReverseType(original.Type),
            ResponsibleUserId = authorizedById,
            Reference = original.Reference,
            Notes = "Reverso de " + original.Id.ToString("N"),
            OccurredAt = now,
            RecordedAt = now
        };
        foreach (var source in original.Lines.OrderBy(line => line.LineNumber))
        {
            var line = new InventoryMovementLine { LineNumber = source.LineNumber, ProductId = source.ProductId, UnitId = source.UnitId, LotId = source.LotId, LotAllocationMode = source.LotAllocationMode };
            switch (original.Type)
            {
                case InventoryMovementType.Entry: line.Quantity = source.Quantity; line.SourceLocationId = source.DestinationLocationId; break;
                case InventoryMovementType.Exit: line.Quantity = source.Quantity; line.DestinationLocationId = source.SourceLocationId; break;
                case InventoryMovementType.Transfer: line.Quantity = source.Quantity; line.SourceLocationId = source.DestinationLocationId; line.DestinationLocationId = source.SourceLocationId; break;
                case InventoryMovementType.Adjustment: break;
            }
            foreach (var change in source.BalanceChanges)
            {
                var lotId = change.LotId ?? legacyLots[source.ProductId].Id;
                var balance = balances[new(source.ProductId, change.LocationId, lotId)];
                var previous = balance.Quantity;
                var delta = -change.DeltaQuantity;
                var resulting = previous + delta;
                if (decimal.Round(resulting, 4) != resulting || Math.Abs(resulting) > 99_999_999_999_999.9999m)
                    throw new InvalidOperationException("El reverso excede la precisión permitida del saldo.");
                line.BalanceChanges.Add(new InventoryBalanceChange { LocationId = change.LocationId, LotId = lotId, LotNumberSnapshot = change.LotNumberSnapshot ?? legacyLots.GetValueOrDefault(source.ProductId)?.Number, LotDateSnapshot = change.LotDateSnapshot ?? legacyLots.GetValueOrDefault(source.ProductId)?.LotDate, DeltaQuantity = delta, PreviousQuantity = previous, ResultingQuantity = resulting });
                balance.Quantity = resulting; balance.UpdatedAt = now;
                if (original.Type == InventoryMovementType.Adjustment)
                {
                    line.PreviousQuantity = previous; line.AdjustmentDelta = delta; line.Quantity = resulting;
                }
            }
            reversal.Lines.Add(line);
        }
        dbContext.InventoryMovements.Add(reversal);
        return reversal;
    }

    private async Task LockOriginalAsync(Guid id, IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is null) return;
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM inventory_movements WHERE id = {id} FOR UPDATE", cancellationToken);
    }

    private async Task<IReadOnlyList<InventoryBalanceResult>> CurrentBalancesAsync(InventoryMovement movement, CancellationToken cancellationToken)
    {
        var keys = movement.Lines.SelectMany(line => line.BalanceChanges.Select(change => new BalanceKey(line.ProductId, change.LocationId, change.LotId))).Distinct().ToArray();
        var products = keys.Select(key => key.ProductId).ToArray(); var locations = keys.Select(key => key.LocationId).ToArray();
        return await dbContext.InventoryBalances.AsNoTracking().Where(item => products.Contains(item.ProductId) && locations.Contains(item.LocationId))
            .Select(item => new InventoryBalanceResult(item.ProductId, item.LocationId, item.LotId, item.Quantity, item.Version, item.Quantity < 0)).ToListAsync(cancellationToken);
    }

    private async Task<InventoryCorrectionResult?> ExistingAsync(Guid operationId, string fingerprint, CancellationToken cancellationToken)
    {
        var item = await dbContext.InventoryMovementCorrections.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (item is null) return null;
        return item.RequestFingerprint == fingerprint
            ? new(InventoryCorrectionStatus.Success, item.Id, item.ReversalMovementId, item.ReplacementMovementId)
            : new(InventoryCorrectionStatus.IdempotencyConflict);
    }

    private static InventoryReplacementCommand? Normalize(InventoryReplacementCommand? value) => value is null ? null : value with
    {
        Reference = string.IsNullOrWhiteSpace(value.Reference) ? null : value.Reference.Trim(),
        Notes = string.IsNullOrWhiteSpace(value.Notes) ? null : value.Notes.Trim(),
        ApprovedSharedAssignments = (value.ApprovedSharedAssignments ?? []).Distinct().OrderBy(item => item.ProductId).ThenBy(item => item.LocationId).ToArray()
    };
    private static InventoryMovementType ReverseType(InventoryMovementType type) => type switch { InventoryMovementType.Entry => InventoryMovementType.Exit, InventoryMovementType.Exit => InventoryMovementType.Entry, InventoryMovementType.Transfer => InventoryMovementType.Transfer, InventoryMovementType.Adjustment => InventoryMovementType.Adjustment, _ => throw new InvalidOperationException() };
    private static InventoryCorrectionResult Map(InventoryMovementResult result) => result.Status switch
    {
        InventoryMovementStatus.InvalidPin => new(InventoryCorrectionStatus.InvalidPin),
        InventoryMovementStatus.RequiresLocationSharingConfirmation => new(InventoryCorrectionStatus.RequiresLocationSharingConfirmation, SharingConflicts: result.Conflicts),
        InventoryMovementStatus.BalanceChanged => new(InventoryCorrectionStatus.BalanceChanged, Errors: result.ValidationErrors),
        InventoryMovementStatus.IdempotencyConflict => new(InventoryCorrectionStatus.IdempotencyConflict),
        _ => new(InventoryCorrectionStatus.ValidationFailed, Errors: result.ValidationErrors)
    };
    private static string CreateFingerprint(InventoryCorrectionCommand command, Guid authorizedById)
    {
        var builder = new StringBuilder().Append(command.OriginalMovementId.ToString("N")).Append('|').Append(command.RequestedByUserId.ToString("N")).Append('|').Append(authorizedById.ToString("N")).Append('|').Append(command.Reason);
        if (command.Replacement is { } replacement)
        {
            builder.Append('|').Append(replacement.Type).Append('|').Append(replacement.Reference).Append('|').Append(replacement.Notes);
            foreach (var line in replacement.Lines) builder.Append('|').Append(line.ProductId.ToString("N")).Append(':').Append(line.Quantity.ToString("G29", CultureInfo.InvariantCulture)).Append(':').Append(line.SourceLocationId).Append(':').Append(line.DestinationLocationId).Append(':').Append(line.LocationId).Append(':').Append(line.ExpectedBalanceVersion);
        }
        return Hash(builder.ToString());
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private async Task<InventoryCorrectionResult> AbortAsync(IDbContextTransaction? transaction, InventoryCorrectionResult result, CancellationToken cancellationToken) { if (transaction is not null) await transaction.RollbackAsync(cancellationToken); dbContext.ChangeTracker.Clear(); return result; }
    private readonly record struct BalanceKey(Guid ProductId, Guid LocationId, Guid? LotId);
}
