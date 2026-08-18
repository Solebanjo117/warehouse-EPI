using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed class InventoryMovementService(
    WarehouseDbContext dbContext,
    UserPinService userPinService,
    TimeProvider timeProvider,
    WarehouseClock? warehouseClock = null)
{
    private readonly InventoryLotEngine lotEngine = new(dbContext);
    private readonly InventoryMovementStore movementStore = new(dbContext, timeProvider);
    private readonly WarehouseClock warehouseClock = warehouseClock ?? new(new WarehouseSettingsService(dbContext));

    public async Task<InventoryMovementResult> ConfirmAsync(
        InventoryMovementCommand command,
        CancellationToken cancellationToken = default)
    {
        var user = await userPinService.AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null || user.Role.Code is not ("ADMIN" or "OPERATOR"))
            return new(InventoryMovementStatus.InvalidPin);

        var normalized = InventoryMovementRules.Normalize(command);
        var structuralErrors = InventoryMovementRules.ValidateStructure(normalized);
        if (structuralErrors.Count > 0)
            return new(InventoryMovementStatus.ValidationFailed, Errors: structuralErrors);

        var productIds = normalized.Lines.Select(line => line.ProductId).Distinct().ToArray();
        var products = await dbContext.Products.AsNoTracking()
            .Include(product => product.BaseUnit)
            .Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);
        var productErrors = InventoryMovementRules.ValidateProductsAndQuantities(normalized, products);
        if (productErrors.Count > 0)
            return new(InventoryMovementStatus.ValidationFailed, Errors: productErrors);

        return await ConfirmTrackedLotsAsync(normalized, user, products, cancellationToken);
    }

    private async Task<InventoryMovementResult> ConfirmTrackedLotsAsync(
        InventoryMovementCommand command,
        User user,
        IReadOnlyDictionary<Guid, Product> products,
        CancellationToken cancellationToken)
    {
        var fingerprint = InventoryMovementRules.CreateFingerprint(command, user.Id);
        var existing = await movementStore.GetExistingResultAsync(command.OperationId, fingerprint, cancellationToken);
        if (existing is not null)
            return existing;

        // Settings can fall back while BusinessSettings is awaiting its reviewed migration.
        // Resolve the warehouse date before opening PostgreSQL's explicit transaction: a missing
        // settings table would otherwise abort that transaction even when its exception is handled.
        var now = timeProvider.GetUtcNow();
        var lotDate = await warehouseClock.GetDateAsync(now, cancellationToken);
        var ownsTransaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : dbContext.Database.CurrentTransaction;
        try
        {
            var pairs = InventoryMovementRules.GetLocationPairs(command).ToArray();
            var locationIds = pairs.Select(pair => pair.LocationId).Distinct().Order().ToArray();
            if (transaction is not null)
                await InventoryMovementStore.LockLocationsAsync(locationIds, transaction, cancellationToken);

            var locations = await dbContext.Locations.AsNoTracking()
                .Where(item => locationIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            var locationErrors = InventoryMovementRules.ValidateLocations(locationIds, locations);
            if (locationErrors.Count != 0)
                return await AbortAsync(transaction, new(InventoryMovementStatus.ValidationFailed, Errors: locationErrors), cancellationToken);

            var conflicts = await movementStore.FindSharingConflictsAsync(
                pairs,
                products,
                locations,
                command.ApprovedSharedAssignments ?? [],
                cancellationToken);
            if (conflicts.Count != 0)
            {
                return await AbortAsync(transaction, new(
                    InventoryMovementStatus.RequiresLocationSharingConfirmation,
                    SharingConflicts: conflicts), cancellationToken);
            }

            var lots = await lotEngine.GetOrCreateDailyLotsAsync(products.Values, lotDate, now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            var balanceKeys = new HashSet<InventoryBalanceKey>();
            foreach (var line in command.Lines)
            {
                var productLots = lots[line.ProductId];
                foreach (var location in InventoryMovementRules.GetLocations(line, command.Type))
                    foreach (var lot in productLots)
                        balanceKeys.Add(new(line.ProductId, location, lot.Id));
            }

            await movementStore.EnsureBalancesExistAsync(balanceKeys, cancellationToken);
            if (transaction is not null)
                await InventoryMovementStore.LockBalancesAsync(balanceKeys, transaction, cancellationToken);
            var balances = await movementStore.LoadTrackedBalancesAsync(balanceKeys, cancellationToken);

            foreach (var line in command.Lines.Where(item => command.Type == InventoryMovementType.Adjustment))
            {
                var related = balances.Where(item => item.Key.ProductId == line.ProductId &&
                        item.Key.LocationId == line.LocationId!.Value)
                    .Select(item => item.Value)
                    .ToArray();
                var token = InventoryLotEngine.AggregateVersion(related);
                var acceptsLegacySingleVersion = related.Length == 1 && line.ExpectedBalanceVersion == related[0].Version;
                var acceptsInitialZero = line.ExpectedBalanceVersion == 0 && related.All(item => item.Quantity == 0);
                if (!acceptsInitialZero && !acceptsLegacySingleVersion && line.ExpectedBalanceVersion != token)
                {
                    return await AbortAsync(transaction, new(InventoryMovementStatus.BalanceChanged,
                        Errors: ["El saldo cambió desde que fue consultado."]), cancellationToken);
                }
            }

            await movementStore.UpsertAssignmentsAsync(pairs, cancellationToken);
            var movement = new InventoryMovement
            {
                OperationId = command.OperationId,
                RequestFingerprint = fingerprint,
                Type = command.Type,
                ResponsibleUserId = user.Id,
                Reference = command.Reference,
                Notes = command.Notes,
                OccurredAt = now,
                RecordedAt = now
            };
            foreach (var (commandLine, index) in command.Lines.Select((item, index) => (item, index)))
            {
                var product = products[commandLine.ProductId];
                var line = new InventoryMovementLine
                {
                    LineNumber = index + 1,
                    ProductId = product.Id,
                    UnitId = product.BaseUnitId,
                    Quantity = commandLine.Quantity,
                    SourceLocationId = commandLine.SourceLocationId,
                    DestinationLocationId = commandLine.DestinationLocationId,
                    LotAllocationMode = command.Type == InventoryMovementType.Entry
                        ? InventoryLotAllocationMode.DailyLot
                        : InventoryLotAllocationMode.AutomaticFefo
                };
                var productLots = lots[product.Id];
                var daily = productLots.Single(item => item.NormalizedNumber == InventoryLotEngine.DailyLotNumber(lotDate));
                InventoryLotEngine.ApplyTrackedLine(command.Type, commandLine, line, balances, productLots, daily, now);
                movement.Lines.Add(line);
            }

            dbContext.InventoryMovements.Add(movement);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (ownsTransaction && transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            var resulting = balances.Values.GroupBy(item => new { item.ProductId, item.LocationId })
                .Select(group => new InventoryBalanceResult(
                    group.Key.ProductId,
                    group.Key.LocationId,
                    null,
                    group.Sum(item => item.Quantity),
                    InventoryLotEngine.AggregateVersion(group),
                    group.Any(item => item.Quantity < 0)))
                .ToArray();
            return new(InventoryMovementStatus.Success, movement.Id, user.Id, user.FullName, resulting);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await AbortAsync(transaction, new(InventoryMovementStatus.BalanceChanged,
                Errors: ["El inventario cambió mientras se confirmaba la operación."]), cancellationToken);
        }
        catch (DbUpdateException exception) when (InventoryMovementStore.IsOperationIdConflict(exception))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return await movementStore.GetExistingResultAsync(command.OperationId, fingerprint, cancellationToken)
                ?? new(InventoryMovementStatus.IdempotencyConflict);
        }
    }

    private async Task<InventoryMovementResult> AbortAsync(
        IDbContextTransaction? transaction,
        InventoryMovementResult result,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
            await transaction.RollbackAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        return result;
    }
}
