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

        return await ConfirmAuthorizedAsync(command, user, cancellationToken: cancellationToken);
    }

    internal async Task<InventoryMovementResult> ConfirmAuthorizedAsync(
        InventoryMovementCommand command, User user, bool allowReservedWip = false,
        Guid? productionSupplyLineId = null, CancellationToken cancellationToken = default)
    {
        if (user.Role.Code is not ("ADMIN" or "OPERATOR"))
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

        return await ConfirmTrackedLotsAsync(normalized, user, products, allowReservedWip, productionSupplyLineId, cancellationToken);
    }

    private async Task<InventoryMovementResult> ConfirmTrackedLotsAsync(
        InventoryMovementCommand command,
        User user,
        IReadOnlyDictionary<Guid, Product> products,
        bool allowReservedWip,
        Guid? productionSupplyLineId,
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
        var rollbackTransaction = ownsTransaction ? transaction : null;
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
            if (command.Purpose == InventoryMovementPurpose.ProductionIssue && command.Lines.Any(line =>
                    line.SourceLocationId is not Guid sourceId || !locations.TryGetValue(sourceId, out var source) ||
                    source.Kind != LocationKind.Rack || source.OperationalRole == LocationOperationalRole.Wip))
                locationErrors.Add("El surtimiento WIP requiere un rack de inventario como origen.");
            if (command.Purpose == InventoryMovementPurpose.WipWarehouseReturn && command.Lines.Any(line =>
                    line.DestinationLocationId is not Guid destinationId ||
                    !locations.TryGetValue(destinationId, out var destination) ||
                    destination.OperationalRole == LocationOperationalRole.Wip))
                locationErrors.Add("El regreso WIP requiere una ubicación de bodega no WIP como destino.");
            if (locationErrors.Count != 0)
                return await AbortAsync(rollbackTransaction, new(InventoryMovementStatus.ValidationFailed, Errors: locationErrors), cancellationToken);

            var warehouseReservationErrors = await ValidateWarehouseReservationsAsync(command, productionSupplyLineId, cancellationToken);
            if (warehouseReservationErrors.Count != 0)
                return await AbortAsync(rollbackTransaction, new(InventoryMovementStatus.ValidationFailed, Errors: warehouseReservationErrors), cancellationToken);

            if (!allowReservedWip && command.Purpose is InventoryMovementPurpose.WipConsumption or
                    InventoryMovementPurpose.WipWarehouseReturn or InventoryMovementPurpose.WipSupplierReturn)
            {
                var freeErrors = await ValidateFreeWipAsync(command, cancellationToken);
                if (freeErrors.Count != 0)
                    return await AbortAsync(rollbackTransaction, new(InventoryMovementStatus.ValidationFailed, Errors: freeErrors), cancellationToken);
                command = await AllocateFreeWipLotsAsync(command, cancellationToken);
            }

            if (command.OperationalAreaId is Guid operationalAreaId)
            {
                var operationalArea = await dbContext.Locations.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == operationalAreaId, cancellationToken);
                if (operationalArea is null || !operationalArea.IsOperational ||
                    operationalArea.OperationalRole != LocationOperationalRole.Wip)
                {
                    return await AbortAsync(rollbackTransaction, new(InventoryMovementStatus.ValidationFailed,
                        Errors: ["La zona WIP indicada no existe o no está disponible."]), cancellationToken);
                }
            }

            var conflicts = await movementStore.FindSharingConflictsAsync(
                pairs,
                products,
                locations,
                command.ApprovedSharedAssignments ?? [],
                cancellationToken);
            if (conflicts.Count != 0)
            {
                return await AbortAsync(rollbackTransaction, new(
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
                    return await AbortAsync(rollbackTransaction, new(InventoryMovementStatus.BalanceChanged,
                        Errors: ["El saldo cambió desde que fue consultado."]), cancellationToken);
                }
            }

            // WIP relationships are deliberately optional. A movement can create a real WIP
            // balance without silently turning it into a catalog assignment.
            var assignablePairs = pairs
                .Where(pair => locations[pair.LocationId].OperationalRole != LocationOperationalRole.Wip)
                .ToArray();
            await movementStore.UpsertAssignmentsAsync(assignablePairs, cancellationToken);
            var movement = new InventoryMovement
            {
                OperationId = command.OperationId,
                RequestFingerprint = fingerprint,
                Type = command.Type,
                Purpose = command.Purpose,
                OperationalAreaId = command.OperationalAreaId,
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
                        ? (commandLine.DestinationLotId.HasValue ? InventoryLotAllocationMode.Explicit : InventoryLotAllocationMode.DailyLot)
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
            return await AbortAsync(rollbackTransaction, new(InventoryMovementStatus.BalanceChanged,
                Errors: ["El inventario cambió mientras se confirmaba la operación."]), cancellationToken);
        }
        catch (DbUpdateException exception) when (InventoryMovementStore.IsOperationIdConflict(exception))
        {
            if (rollbackTransaction is not null)
                await rollbackTransaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return await movementStore.GetExistingResultAsync(command.OperationId, fingerprint, cancellationToken)
                ?? new(InventoryMovementStatus.IdempotencyConflict);
        }
    }

    private async Task<List<string>> ValidateFreeWipAsync(InventoryMovementCommand command, CancellationToken token)
    {
        var errors = new List<string>();
        var reversed = await dbContext.ProductionMaterialOperations.AsNoTracking()
            .Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        foreach (var group in command.Lines.GroupBy(x => new { x.ProductId, LocationId = x.SourceLocationId!.Value }))
        {
            var physical = await dbContext.InventoryBalances.AsNoTracking()
                .Where(x => x.ProductId == group.Key.ProductId && x.LocationId == group.Key.LocationId)
                .SumAsync(x => x.Quantity, token);
            var links = await dbContext.ProductionMaterialIssueLinks.AsNoTracking()
                .Include(x => x.InventoryMovementLine).Include(x => x.OperationLines).ThenInclude(x => x.Operation)
                .Where(x => x.InventoryMovementLine.ProductId == group.Key.ProductId &&
                    x.InventoryMovementLine.DestinationLocationId == group.Key.LocationId).ToListAsync(token);
            var reserved = links.Sum(link => link.InventoryMovementLine.Quantity - link.OperationLines
                .Where(line => line.Operation.Type != ProductionMaterialOperationType.Reversal && !reversed.Contains(line.Operation.Id))
                .Sum(line => line.Quantity));
            var requested = group.Sum(x => x.Quantity);
            if (requested > physical - reserved)
                errors.Add("La cantidad supera el saldo WIP libre. El resto está reservado para órdenes de trabajo.");
        }
        return errors;
    }

    private async Task<List<string>> ValidateWarehouseReservationsAsync(InventoryMovementCommand command,
        Guid? ownSupplyLineId, CancellationToken token)
    {
        var outgoing = command.Lines.Where(x => x.SourceLocationId.HasValue)
            .GroupBy(x => new { x.ProductId, LocationId = x.SourceLocationId!.Value });
        var errors = new List<string>();
        foreach (var group in outgoing)
        {
            var reservations = await dbContext.ProductionWarehouseReservations.AsNoTracking()
                .Where(x => x.SupplyRequestLine.ProductId == group.Key.ProductId && x.LocationId == group.Key.LocationId && x.Quantity > x.ReleasedQuantity)
                .Select(x => new { x.SupplyRequestLineId, Remaining = x.Quantity - x.ReleasedQuantity }).ToListAsync(token);
            var otherReserved = reservations.Where(x => x.SupplyRequestLineId != ownSupplyLineId).Sum(x => x.Remaining);
            if (otherReserved <= 0) continue;
            var physical = await dbContext.InventoryBalances.AsNoTracking()
                .Where(x => x.ProductId == group.Key.ProductId && x.LocationId == group.Key.LocationId).SumAsync(x => x.Quantity, token);
            if (group.Sum(x => x.Quantity) > Math.Max(0, physical - otherReserved))
                errors.Add("La cantidad utilizaría material reservado para otra orden. Cambia el origen o solicita una resolución ADMIN.");
        }
        return errors;
    }

    private async Task<InventoryMovementCommand> AllocateFreeWipLotsAsync(
        InventoryMovementCommand command, CancellationToken token)
    {
        var reversed = await dbContext.ProductionMaterialOperations.AsNoTracking()
            .Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var result = new List<InventoryMovementLineCommand>();
        var allocated = new Dictionary<(Guid ProductId, Guid LocationId, Guid LotId), decimal>();
        foreach (var line in command.Lines)
        {
            var locationId = line.SourceLocationId!.Value;
            var balances = await dbContext.InventoryBalances.AsNoTracking()
                .Include(x => x.Lot)
                .Where(x => x.ProductId == line.ProductId && x.LocationId == locationId)
                .OrderBy(x => x.Lot!.LotDate == null).ThenBy(x => x.Lot!.LotDate)
                .ThenBy(x => x.Lot!.CreatedAt).ThenBy(x => x.Lot!.NormalizedNumber)
                .ToListAsync(token);
            var links = await dbContext.ProductionMaterialIssueLinks.AsNoTracking()
                .Include(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
                .Include(x => x.OperationLines).ThenInclude(x => x.Operation)
                .Include(x => x.OperationLines).ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
                .Where(x => x.InventoryMovementLine.ProductId == line.ProductId &&
                    x.InventoryMovementLine.DestinationLocationId == locationId).ToListAsync(token);
            var reserved = links.SelectMany(link => RemainingLots(link, reversed))
                .GroupBy(x => x.LotId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
            var remaining = line.Quantity;
            var selected = new List<InventoryLotSelection>();
            foreach (var balance in balances)
            {
                var key = (line.ProductId, locationId, balance.LotId!.Value);
                var free = balance.Quantity - reserved.GetValueOrDefault(balance.LotId.Value) - allocated.GetValueOrDefault(key);
                var take = Math.Min(remaining, Math.Max(0, free));
                if (take > 0)
                {
                    selected.Add(new(balance.LotId.Value, take));
                    allocated[key] = allocated.GetValueOrDefault(key) + take;
                }
                remaining -= take;
                if (remaining == 0) break;
            }
            result.Add(line with { Lots = selected });
        }
        return command with { Lines = result };
    }

    internal static IEnumerable<InventoryLotSelection> RemainingLots(
        ProductionMaterialIssueLink link, IReadOnlyCollection<Guid> reversed)
    {
        var used = link.OperationLines
            .Where(x => x.Operation.Type != ProductionMaterialOperationType.Reversal && !reversed.Contains(x.Operation.Id))
            .SelectMany(x => x.InventoryMovementLine.BalanceChanges)
            .Where(x => x.LocationId == link.InventoryMovementLine.DestinationLocationId && x.DeltaQuantity < 0)
            .GroupBy(x => x.LotId!.Value).ToDictionary(x => x.Key, x => -x.Sum(y => y.DeltaQuantity));
        return link.InventoryMovementLine.BalanceChanges
            .Where(x => x.LocationId == link.InventoryMovementLine.DestinationLocationId && x.DeltaQuantity > 0)
            .OrderBy(x => x.LotDateSnapshot == null).ThenBy(x => x.LotDateSnapshot).ThenBy(x => x.LotNumberSnapshot)
            .Select(x => new InventoryLotSelection(x.LotId!.Value,
                Math.Max(0, x.DeltaQuantity - used.GetValueOrDefault(x.LotId.Value))))
            .Where(x => x.Quantity > 0);
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
