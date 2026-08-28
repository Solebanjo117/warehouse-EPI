using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed class WipDispositionService(
    WarehouseDbContext dbContext,
    UserPinService userPinService,
    TimeProvider timeProvider)
{
    private readonly InventoryMovementStore movementStore = new(dbContext, timeProvider);

    public async Task<WipDispositionResult> ConfirmAsync(
        WipDispositionCommand command,
        CancellationToken cancellationToken = default)
    {
        var normalized = command with
        {
            Reference = Normalize(command.Reference),
            Notes = Normalize(command.Notes),
            ApprovedSharedAssignments = (command.ApprovedSharedAssignments ?? []).Distinct()
                .OrderBy(item => item.ProductId).ThenBy(item => item.LocationId).ToArray()
        };
        var errors = Validate(normalized);
        if (errors.Count != 0)
            return new(WipDispositionStatus.ValidationFailed, Errors: errors);

        var user = await userPinService.AuthenticateAsync(normalized.Pin, cancellationToken);
        if (user is null)
            return new(WipDispositionStatus.InvalidPin);

        var fingerprint = CreateFingerprint(normalized, user.Id);
        var existing = await ExistingAsync(normalized.OperationId, fingerprint, cancellationToken);
        if (existing is not null)
            return existing;

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await LockOriginalAsync(normalized.OriginalMovementLineId, transaction, cancellationToken);
            var original = await dbContext.InventoryMovementLines
                .Include(line => line.Movement).ThenInclude(movement => movement.OperationalArea)
                .Include(line => line.Product).ThenInclude(product => product.BaseUnit)
                .Include(line => line.BalanceChanges)
                .SingleOrDefaultAsync(line => line.Id == normalized.OriginalMovementLineId, cancellationToken);
            if (original is null || original.Movement.Purpose != InventoryMovementPurpose.ProductionIssue ||
                original.Movement.Type != InventoryMovementType.Exit || original.Movement.OperationalArea is null)
            {
                return await AbortAsync(transaction, new(WipDispositionStatus.ValidationFailed,
                    Errors: ["La salida WIP original no existe o no es válida."]), cancellationToken);
            }
            if (await dbContext.InventoryMovementCorrections.AnyAsync(
                    correction => correction.OriginalMovementId == original.MovementId, cancellationToken))
            {
                return await AbortAsync(transaction, new(WipDispositionStatus.ValidationFailed,
                    Errors: ["La salida WIP original fue corregida y ya no admite devoluciones."]), cancellationToken);
            }

            var effective = await EffectiveDispositionsAsync(original.Id, cancellationToken);
            var returned = effective.Sum(item => item.Quantity);
            var remaining = original.Quantity - returned;
            if (normalized.Quantity > remaining)
            {
                return await AbortAsync(transaction, new(WipDispositionStatus.ValidationFailed,
                    RemainingQuantity: remaining,
                    Errors: [$"La cantidad supera el máximo disponible de {remaining:0.####}."]), cancellationToken);
            }

            Location? destination = null;
            InventoryMovement? inventoryMovement = null;
            IReadOnlyList<InventoryBalanceResult> resultingBalances = [];
            if (normalized.Type == WipDispositionType.WarehouseReturn)
            {
                destination = await dbContext.Locations.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == normalized.DestinationLocationId, cancellationToken);
                if (destination is null || !destination.IsOperational || !destination.TracksInventory)
                {
                    return await AbortAsync(transaction, new(WipDispositionStatus.ValidationFailed,
                        Errors: ["La ubicación destino no existe, está inactiva o está bloqueada."]), cancellationToken);
                }

                var pair = new InventoryAssignmentKey(original.ProductId, destination.Id);
                var products = new Dictionary<Guid, Product> { [original.ProductId] = original.Product };
                var locations = new Dictionary<Guid, Location> { [destination.Id] = destination };
                var conflicts = await movementStore.FindSharingConflictsAsync(
                    [pair], products, locations, normalized.ApprovedSharedAssignments ?? [], cancellationToken);
                if (conflicts.Count != 0)
                {
                    return await AbortAsync(transaction, new(WipDispositionStatus.RequiresLocationSharingConfirmation,
                        RemainingQuantity: remaining, SharingConflicts: conflicts), cancellationToken);
                }

                var allocations = Allocate(original, returned, normalized.Quantity);
                var balanceKeys = allocations.Select(item =>
                    new InventoryBalanceKey(original.ProductId, destination.Id, item.LotId)).ToArray();
                if (transaction is not null)
                    await InventoryMovementStore.LockLocationsAsync([destination.Id], transaction, cancellationToken);
                await movementStore.EnsureBalancesExistAsync(balanceKeys, cancellationToken);
                if (transaction is not null)
                    await InventoryMovementStore.LockBalancesAsync(balanceKeys, transaction, cancellationToken);
                var balances = await movementStore.LoadTrackedBalancesAsync(balanceKeys, cancellationToken);
                await movementStore.UpsertAssignmentsAsync([pair], cancellationToken);

                var now = timeProvider.GetUtcNow();
                inventoryMovement = new InventoryMovement
                {
                    OperationId = Guid.NewGuid(),
                    RequestFingerprint = InventoryFingerprint.Hash(fingerprint + "|inventory"),
                    Type = InventoryMovementType.Entry,
                    Purpose = InventoryMovementPurpose.WipWarehouseReturn,
                    ResponsibleUserId = user.Id,
                    Reference = normalized.Reference,
                    Notes = normalized.Notes,
                    OccurredAt = now,
                    RecordedAt = now
                };
                var line = new InventoryMovementLine
                {
                    LineNumber = 1,
                    ProductId = original.ProductId,
                    UnitId = original.UnitId,
                    Quantity = normalized.Quantity,
                    DestinationLocationId = destination.Id,
                    LotAllocationMode = InventoryLotAllocationMode.AutomaticFefo
                };
                foreach (var allocation in allocations)
                {
                    var balance = balances[new(original.ProductId, destination.Id, allocation.LotId)];
                    var previous = balance.Quantity;
                    var result = previous + allocation.Quantity;
                    line.BalanceChanges.Add(new InventoryBalanceChange
                    {
                        LocationId = destination.Id,
                        LotId = allocation.LotId,
                        LotNumberSnapshot = allocation.LotNumber,
                        LotDateSnapshot = allocation.LotDate,
                        DeltaQuantity = allocation.Quantity,
                        PreviousQuantity = previous,
                        ResultingQuantity = result
                    });
                    balance.Quantity = result;
                    balance.UpdatedAt = now;
                }
                inventoryMovement.Lines.Add(line);
                dbContext.InventoryMovements.Add(inventoryMovement);
                resultingBalances = balances.Values.Select(balance => new InventoryBalanceResult(
                    balance.ProductId, balance.LocationId, balance.LotId, balance.Quantity,
                    balance.Version, balance.Quantity < 0)).ToArray();
            }

            var disposition = new WipDisposition
            {
                OperationId = normalized.OperationId,
                RequestFingerprint = fingerprint,
                OriginalMovementLineId = original.Id,
                Type = normalized.Type,
                Quantity = normalized.Quantity,
                ResponsibleUserId = user.Id,
                DestinationLocationId = destination?.Id,
                InventoryMovement = inventoryMovement,
                Reference = normalized.Reference,
                Notes = normalized.Notes,
                OccurredAt = timeProvider.GetUtcNow(),
                RecordedAt = timeProvider.GetUtcNow()
            };
            dbContext.WipDispositions.Add(disposition);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new(WipDispositionStatus.Success, disposition.Id, inventoryMovement?.Id,
                remaining - normalized.Quantity, resultingBalances);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return await ExistingAsync(normalized.OperationId, fingerprint, cancellationToken)
                ?? new(WipDispositionStatus.IdempotencyConflict);
        }
    }

    private static List<string> Validate(WipDispositionCommand command)
    {
        var errors = new List<string>();
        if (command.OperationId == Guid.Empty || command.OriginalMovementLineId == Guid.Empty)
            errors.Add("La operación y la salida WIP original son obligatorias.");
        if (command.Quantity <= 0 || decimal.Round(command.Quantity, 4) != command.Quantity ||
            command.Quantity > InventoryMovementRules.MaximumQuantity)
            errors.Add("La cantidad debe ser positiva y admitir como máximo cuatro decimales.");
        if (command.Type == WipDispositionType.WarehouseReturn && command.DestinationLocationId is null)
            errors.Add("Selecciona la ubicación destino para la devolución a bodega.");
        if (command.Type == WipDispositionType.SupplierReturn && command.DestinationLocationId is not null)
            errors.Add("Una devolución a proveedor no admite ubicación destino.");
        if (command.Type == WipDispositionType.SupplierReturn && string.IsNullOrWhiteSpace(command.Reference))
            errors.Add("La referencia documental del proveedor es obligatoria.");
        if (command.Reference?.Length > 120 || command.Notes?.Length > 500)
            errors.Add("La referencia admite 120 caracteres y las observaciones 500.");
        return errors;
    }

    private async Task<IReadOnlyList<WipDisposition>> EffectiveDispositionsAsync(Guid lineId, CancellationToken token)
    {
        var items = await dbContext.WipDispositions.AsNoTracking()
            .Where(item => item.OriginalMovementLineId == lineId).ToListAsync(token);
        var reversed = items.Where(item => item.ReversesDispositionId != null)
            .Select(item => item.ReversesDispositionId!.Value).ToHashSet();
        return items.Where(item => item.ReversesDispositionId is null && !reversed.Contains(item.Id)).ToArray();
    }

    private static IReadOnlyList<ReturnAllocation> Allocate(
        InventoryMovementLine original,
        decimal alreadyReturned,
        decimal quantity)
    {
        var allocations = original.BalanceChanges.Where(change => change.DeltaQuantity < 0)
            .GroupBy(change => new { change.LotId, change.LotNumberSnapshot, change.LotDateSnapshot })
            .Select(group => new ReturnAllocation(group.Key.LotId!.Value, group.Key.LotNumberSnapshot,
                group.Key.LotDateSnapshot, -group.Sum(item => item.DeltaQuantity)))
            .OrderByDescending(item => item.LotDate).ThenByDescending(item => item.LotNumber, StringComparer.Ordinal)
            .ToArray();
        var skip = alreadyReturned;
        var needed = quantity;
        var result = new List<ReturnAllocation>();
        foreach (var allocation in allocations)
        {
            var available = allocation.Quantity;
            if (skip >= available) { skip -= available; continue; }
            available -= skip; skip = 0;
            var take = Math.Min(available, needed);
            if (take > 0) result.Add(allocation with { Quantity = take });
            needed -= take;
            if (needed == 0) break;
        }
        if (needed != 0)
            throw new InvalidOperationException("La salida WIP no contiene asignaciones de lote suficientes.");
        return result;
    }

    private async Task<WipDispositionResult?> ExistingAsync(Guid operationId, string fingerprint, CancellationToken token)
    {
        var item = await dbContext.WipDispositions.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.OperationId == operationId, token);
        if (item is null) return null;
        return item.RequestFingerprint == fingerprint
            ? new(WipDispositionStatus.Success, item.Id, item.InventoryMovementId)
            : new(WipDispositionStatus.IdempotencyConflict);
    }

    private async Task LockOriginalAsync(Guid id, IDbContextTransaction? transaction, CancellationToken token)
    {
        if (transaction is not null)
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM inventory_movement_lines WHERE id = {id} FOR UPDATE", token);
    }

    private async Task<WipDispositionResult> AbortAsync(
        IDbContextTransaction? transaction, WipDispositionResult result, CancellationToken token)
    {
        if (transaction is not null) await transaction.RollbackAsync(token);
        dbContext.ChangeTracker.Clear();
        return result;
    }

    private static string CreateFingerprint(WipDispositionCommand command, Guid userId)
    {
        var builder = new StringBuilder().Append(userId.ToString("N")).Append('|')
            .Append(command.OriginalMovementLineId.ToString("N")).Append('|').Append(command.Type).Append('|')
            .Append(command.Quantity.ToString("G29", CultureInfo.InvariantCulture)).Append('|')
            .Append(command.DestinationLocationId).Append('|').Append(command.Reference).Append('|').Append(command.Notes);
        foreach (var approval in command.ApprovedSharedAssignments ?? [])
            builder.Append('|').Append(approval.ProductId).Append(':').Append(approval.LocationId);
        return InventoryFingerprint.Hash(builder.ToString());
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private sealed record ReturnAllocation(Guid LotId, string? LotNumber, DateOnly? LotDate, decimal Quantity);
}
