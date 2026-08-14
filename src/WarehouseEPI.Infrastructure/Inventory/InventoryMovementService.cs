using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed class InventoryMovementService(
    WarehouseDbContext dbContext,
    UserPinService userPinService,
    TimeProvider timeProvider)
{
    private const decimal MaximumQuantity = 99_999_999_999_999.9999m;

    public async Task<InventoryMovementResult> ConfirmAsync(
        InventoryMovementCommand command,
        CancellationToken cancellationToken = default)
    {
        var user = await userPinService.AuthenticateAsync(command.Pin, cancellationToken);
        if (user is null || user.Role.Code is not ("ADMIN" or "OPERATOR"))
            return new(InventoryMovementStatus.InvalidPin);

        var normalized = Normalize(command);
        var structuralErrors = ValidateStructure(normalized);
        if (structuralErrors.Count > 0)
            return new(InventoryMovementStatus.ValidationFailed, Errors: structuralErrors);

        var productIds = normalized.Lines.Select(line => line.ProductId).Distinct().ToArray();
        var products = await dbContext.Products.AsNoTracking()
            .Include(product => product.BaseUnit)
            .Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        var productErrors = ValidateProductsAndQuantities(normalized, products);
        if (productErrors.Count > 0)
            return new(InventoryMovementStatus.ValidationFailed, Errors: productErrors);
        if (products.Count != 0)
            return await ConfirmTrackedLotsAsync(normalized, user, products, cancellationToken);

        var fingerprint = CreateFingerprint(normalized, user.Id);
        var existingResult = await GetExistingResultAsync(
            normalized.OperationId,
            fingerprint,
            cancellationToken);
        if (existingResult is not null)
            return existingResult;

        var ownsTransaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : dbContext.Database.CurrentTransaction;

        try
        {
            var stillAuthorized = await dbContext.Users.AsNoTracking()
                .AnyAsync(candidate => candidate.Id == user.Id && candidate.IsActive &&
                    (candidate.Role.Code == "ADMIN" || candidate.Role.Code == "OPERATOR"), cancellationToken);
            if (!stillAuthorized)
                return await AbortAsync(transaction, new(InventoryMovementStatus.InvalidPin), cancellationToken);

            existingResult = await GetExistingResultAsync(normalized.OperationId, fingerprint, cancellationToken);
            if (existingResult is not null)
                return await AbortAsync(transaction, existingResult, cancellationToken);

            var balanceKeys = GetBalanceKeys(normalized);
            if (balanceKeys.Count != balanceKeys.Distinct().Count())
            {
                return await AbortAsync(transaction, new(
                    InventoryMovementStatus.ValidationFailed,
                    Errors: ["Una misma combinación de producto y ubicación no puede repetirse dentro de la operación."]), cancellationToken);
            }

            var locationIds = balanceKeys.Select(key => key.LocationId).Distinct().Order().ToArray();
            if (transaction is not null)
                await LockRowsAsync("locations", locationIds, transaction, cancellationToken);

            var locations = await dbContext.Locations.AsNoTracking()
                .Where(location => locationIds.Contains(location.Id))
                .ToDictionaryAsync(location => location.Id, cancellationToken);
            var locationErrors = ValidateLocations(locationIds, locations);
            if (locationErrors.Count > 0)
                return await AbortAsync(transaction, new(InventoryMovementStatus.ValidationFailed, Errors: locationErrors), cancellationToken);

            var assignmentPairs = balanceKeys
                .Select(key => new AssignmentKey(key.ProductId, key.LocationId))
                .Distinct()
                .ToArray();
            var conflicts = await FindSharingConflictsAsync(
                assignmentPairs,
                products,
                locations,
                normalized.ApprovedSharedAssignments ?? [],
                cancellationToken);
            if (conflicts.Count > 0)
            {
                return await AbortAsync(transaction, new(
                    InventoryMovementStatus.RequiresLocationSharingConfirmation,
                    SharingConflicts: conflicts), cancellationToken);
            }

            var createdBalanceKeys = await EnsureBalancesExistAsync(balanceKeys, cancellationToken);
            if (transaction is not null)
                await LockBalancesAsync(balanceKeys, transaction, cancellationToken);

            var balanceProductIds = balanceKeys.Select(key => key.ProductId).Distinct().ToArray();
            var balanceLocationIds = balanceKeys.Select(key => key.LocationId).Distinct().ToArray();
            var loadedBalances = await dbContext.InventoryBalances
                .Where(balance => balanceProductIds.Contains(balance.ProductId) &&
                    balanceLocationIds.Contains(balance.LocationId) && balance.LotId == null)
                .ToListAsync(cancellationToken);
            var balances = loadedBalances
                .Where(balance => balanceKeys.Contains(new BalanceKey(balance.ProductId, balance.LocationId, balance.LotId)))
                .ToDictionary(balance => new BalanceKey(balance.ProductId, balance.LocationId, balance.LotId));

            if (balances.Count != balanceKeys.Count)
                throw new InvalidOperationException("No fue posible preparar todos los saldos del movimiento.");

            foreach (var (line, index) in normalized.Lines.Select((line, index) => (line, index)))
            {
                if (normalized.Type != InventoryMovementType.Adjustment)
                    continue;

                var balance = balances[new BalanceKey(line.ProductId, line.LocationId!.Value, null)];
                var key = new BalanceKey(line.ProductId, line.LocationId!.Value, null);
                var acceptsMissingBalance = line.ExpectedBalanceVersion == 0 && createdBalanceKeys.Contains(key);
                if (!acceptsMissingBalance && line.ExpectedBalanceVersion != balance.Version)
                {
                    return await AbortAsync(transaction, new(
                        InventoryMovementStatus.BalanceChanged,
                        Errors: [$"El saldo de la línea {index + 1} cambió desde que fue consultado."]), cancellationToken);
                }
            }

            await UpsertAssignmentsAsync(assignmentPairs, cancellationToken);

            var now = timeProvider.GetUtcNow();
            var movement = new InventoryMovement
            {
                OperationId = normalized.OperationId,
                RequestFingerprint = fingerprint,
                Type = normalized.Type,
                ResponsibleUserId = user.Id,
                Reference = normalized.Reference,
                Notes = normalized.Notes,
                OccurredAt = now,
                RecordedAt = now
            };

            foreach (var (commandLine, index) in normalized.Lines.Select((line, index) => (line, index)))
            {
                var product = products[commandLine.ProductId];
                var movementLine = new InventoryMovementLine
                {
                    LineNumber = index + 1,
                    ProductId = product.Id,
                    UnitId = product.BaseUnitId,
                    Quantity = commandLine.Quantity,
                    SourceLocationId = commandLine.SourceLocationId,
                    DestinationLocationId = commandLine.DestinationLocationId,
                    LotId = null
                };

                ApplyLine(normalized.Type, commandLine, movementLine, balances, now);
                movement.Lines.Add(movementLine);
            }

            dbContext.InventoryMovements.Add(movement);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (ownsTransaction && transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                await transaction.DisposeAsync();
            }

            return new(
                InventoryMovementStatus.Success,
                movement.Id,
                user.Id,
                user.FullName,
                ToBalanceResults(balanceKeys, balances));
        }
        catch (DbUpdateConcurrencyException)
        {
            return await AbortAsync(transaction, new(
                InventoryMovementStatus.BalanceChanged,
                Errors: ["El inventario cambió mientras se confirmaba la operación."]), cancellationToken);
        }
        catch (InventoryQuantityOutOfRangeException exception)
        {
            return await AbortAsync(transaction, new(
                InventoryMovementStatus.ValidationFailed,
                Errors: [exception.Message]), cancellationToken);
        }
        catch (DbUpdateException exception) when (IsOperationIdConflict(exception))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return await GetExistingResultAsync(normalized.OperationId, fingerprint, cancellationToken) ??
                new(InventoryMovementStatus.IdempotencyConflict);
        }
    }

    private async Task<InventoryMovementResult> ConfirmTrackedLotsAsync(
        InventoryMovementCommand command,
        User user,
        IReadOnlyDictionary<Guid, Product> products,
        CancellationToken cancellationToken)
    {
        var fingerprint = CreateFingerprint(command, user.Id);
        var existing = await GetExistingResultAsync(command.OperationId, fingerprint, cancellationToken);
        if (existing is not null) return existing;

        var ownsTransaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : dbContext.Database.CurrentTransaction;
        try
        {
            var pairs = GetLocationPairs(command).ToArray();
            var locationIds = pairs.Select(pair => pair.LocationId).Distinct().Order().ToArray();
            if (transaction is not null) await LockRowsAsync("locations", locationIds, transaction, cancellationToken);
            var locations = await dbContext.Locations.AsNoTracking().Where(item => locationIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            var locationErrors = ValidateLocations(locationIds, locations);
            if (locationErrors.Count != 0)
                return await AbortAsync(transaction, new(InventoryMovementStatus.ValidationFailed, Errors: locationErrors), cancellationToken);

            var conflicts = await FindSharingConflictsAsync(pairs, products, locations,
                command.ApprovedSharedAssignments ?? [], cancellationToken);
            if (conflicts.Count != 0)
                return await AbortAsync(transaction, new(InventoryMovementStatus.RequiresLocationSharingConfirmation,
                    SharingConflicts: conflicts), cancellationToken);

            var now = timeProvider.GetUtcNow();
            var lotDate = GetWarehouseDate(now);
            var lots = new Dictionary<Guid, List<ProductLot>>();
            foreach (var product in products.Values)
                lots[product.Id] = await GetOrCreateDailyLotAsync(product.Id, lotDate, now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            var balanceKeys = new HashSet<BalanceKey>();
            foreach (var line in command.Lines)
            {
                var productLots = lots[line.ProductId];
                foreach (var location in GetLocations(line, command.Type))
                    foreach (var lot in productLots)
                        balanceKeys.Add(new(line.ProductId, location, lot.Id));
            }
            await EnsureBalancesExistAsync(balanceKeys, cancellationToken);
            if (transaction is not null) await LockBalancesAsync(balanceKeys, transaction, cancellationToken);

            var productIds = balanceKeys.Select(key => key.ProductId).Distinct().ToArray();
            var allBalanceLocations = balanceKeys.Select(key => key.LocationId).Distinct().ToArray();
            var balances = await dbContext.InventoryBalances
                .Where(item => productIds.Contains(item.ProductId) && allBalanceLocations.Contains(item.LocationId) && item.LotId != null)
                .ToDictionaryAsync(item => new BalanceKey(item.ProductId, item.LocationId, item.LotId), cancellationToken);

            foreach (var line in command.Lines.Where(item => command.Type == InventoryMovementType.Adjustment))
            {
                var related = balances.Where(item => item.Key.ProductId == line.ProductId && item.Key.LocationId == line.LocationId!.Value)
                    .Select(item => item.Value).ToArray();
                var token = AggregateVersion(related);
                var acceptsLegacySingleVersion = related.Length == 1 && line.ExpectedBalanceVersion == related[0].Version;
                var acceptsInitialZero = line.ExpectedBalanceVersion == 0 && related.All(item => item.Quantity == 0);
                if (!acceptsInitialZero && !acceptsLegacySingleVersion && line.ExpectedBalanceVersion != token)
                    return await AbortAsync(transaction, new(InventoryMovementStatus.BalanceChanged,
                        Errors: ["El saldo cambió desde que fue consultado."]), cancellationToken);
            }

            await UpsertAssignmentsAsync(pairs, cancellationToken);
            var movement = new InventoryMovement
            {
                OperationId = command.OperationId, RequestFingerprint = fingerprint, Type = command.Type,
                ResponsibleUserId = user.Id, Reference = command.Reference, Notes = command.Notes,
                OccurredAt = now, RecordedAt = now
            };
            foreach (var (commandLine, index) in command.Lines.Select((item, index) => (item, index)))
            {
                var product = products[commandLine.ProductId];
                var line = new InventoryMovementLine
                {
                    LineNumber = index + 1, ProductId = product.Id, UnitId = product.BaseUnitId,
                    Quantity = commandLine.Quantity, SourceLocationId = commandLine.SourceLocationId,
                    DestinationLocationId = commandLine.DestinationLocationId,
                    LotAllocationMode = command.Type == InventoryMovementType.Entry
                        ? InventoryLotAllocationMode.DailyLot : InventoryLotAllocationMode.AutomaticFefo
                };
                var productLots = lots[product.Id];
                var daily = productLots.Single(item => item.NormalizedNumber == DailyLotNumber(lotDate));
                ApplyTrackedLine(command.Type, commandLine, line, balances, productLots, daily, now);
                movement.Lines.Add(line);
            }
            dbContext.InventoryMovements.Add(movement);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (ownsTransaction && transaction is not null) await transaction.CommitAsync(cancellationToken);
            var resulting = balances.Values.GroupBy(item => new { item.ProductId, item.LocationId })
                .Select(group => new InventoryBalanceResult(group.Key.ProductId, group.Key.LocationId, null,
                    group.Sum(item => item.Quantity), AggregateVersion(group), group.Any(item => item.Quantity < 0))).ToArray();
            return new(InventoryMovementStatus.Success, movement.Id, user.Id, user.FullName, resulting);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await AbortAsync(transaction, new(InventoryMovementStatus.BalanceChanged,
                Errors: ["El inventario cambió mientras se confirmaba la operación."]), cancellationToken);
        }
        catch (DbUpdateException exception) when (IsOperationIdConflict(exception))
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return await GetExistingResultAsync(command.OperationId, fingerprint, cancellationToken)
                ?? new(InventoryMovementStatus.IdempotencyConflict);
        }
    }

    private async Task<List<ProductLot>> GetOrCreateDailyLotAsync(Guid productId, DateOnly lotDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var number = DailyLotNumber(lotDate);
        var existing = await dbContext.ProductLots.Where(item => item.ProductId == productId).ToListAsync(cancellationToken);
        if (existing.All(item => item.NormalizedNumber != number))
        {
            existing.Add(new ProductLot { ProductId = productId, Number = number, NormalizedNumber = number, LotDate = lotDate, CreatedAt = now });
            dbContext.ProductLots.Add(existing[^1]);
        }
        return existing;
    }

    private static string DailyLotNumber(DateOnly lotDate) => $"AUTO-{lotDate:yyyyMMdd}";

    private static DateOnly GetWarehouseDate(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Matamoros");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }

    private static IEnumerable<Guid> GetLocations(InventoryMovementLineCommand line, InventoryMovementType type) => type switch
    {
        InventoryMovementType.Entry => [line.DestinationLocationId!.Value],
        InventoryMovementType.Exit => [line.SourceLocationId!.Value],
        InventoryMovementType.Transfer => [line.SourceLocationId!.Value, line.DestinationLocationId!.Value],
        _ => [line.LocationId!.Value]
    };

    private static IEnumerable<AssignmentKey> GetLocationPairs(InventoryMovementCommand command) => command.Lines
        .SelectMany(line => GetLocations(line, command.Type).Select(location => new AssignmentKey(line.ProductId, location)))
        .Distinct();

    private static uint AggregateVersion(IEnumerable<InventoryBalance> balances)
    {
        var text = string.Join('|', balances.OrderBy(item => item.LotId).Select(item =>
            $"{item.LotId:N}:{item.Quantity.ToString("G29", CultureInfo.InvariantCulture)}:{item.Version}"));
        if (text.Length == 0) return 0;
        return BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
    }

    private static void ApplyTrackedLine(InventoryMovementType type, InventoryMovementLineCommand command,
        InventoryMovementLine line, IReadOnlyDictionary<BalanceKey, InventoryBalance> balances,
        IReadOnlyList<ProductLot> lots, ProductLot daily, DateTimeOffset now)
    {
        InventoryBalance Balance(Guid location, ProductLot lot) => balances[new(command.ProductId, location, lot.Id)];
        var ordered = lots.OrderBy(item => item.LotDate is null).ThenBy(item => item.LotDate)
            .ThenBy(item => item.CreatedAt).ThenBy(item => item.NormalizedNumber, StringComparer.Ordinal).ToArray();
        void Add(InventoryBalance balance, ProductLot lot, decimal delta) => ApplyChange(line, balance, delta, now, lot);
        IEnumerable<(InventoryBalance Balance, ProductLot Lot, decimal Delta)> Consume(Guid location, decimal quantity)
        {
            var remaining = quantity;
            ProductLot? last = null;
            foreach (var lot in ordered)
            {
                var balance = Balance(location, lot);
                if (balance.Quantity <= 0) continue;
                var take = Math.Min(remaining, balance.Quantity);
                if (take > 0) { remaining -= take; last = lot; yield return (balance, lot, -take); }
                if (remaining == 0) yield break;
            }
            var fallback = last ?? ordered.FirstOrDefault() ?? daily;
            if (remaining > 0) yield return (Balance(location, fallback), fallback, -remaining);
        }
        switch (type)
        {
            case InventoryMovementType.Entry:
                Add(Balance(command.DestinationLocationId!.Value, daily), daily, command.Quantity); break;
            case InventoryMovementType.Exit:
                foreach (var change in Consume(command.SourceLocationId!.Value, command.Quantity)) Add(change.Balance, change.Lot, change.Delta); break;
            case InventoryMovementType.Transfer:
                foreach (var change in Consume(command.SourceLocationId!.Value, command.Quantity))
                {
                    Add(change.Balance, change.Lot, change.Delta);
                    Add(Balance(command.DestinationLocationId!.Value, change.Lot), change.Lot, -change.Delta);
                }
                break;
            case InventoryMovementType.Adjustment:
                var current = lots.Sum(lot => Balance(command.LocationId!.Value, lot).Quantity);
                var delta = command.Quantity - current;
                line.PreviousQuantity = current; line.AdjustmentDelta = delta;
                if (delta >= 0) Add(Balance(command.LocationId!.Value, daily), daily, delta);
                else foreach (var change in Consume(command.LocationId!.Value, -delta)) Add(change.Balance, change.Lot, change.Delta);
                break;
        }
    }

    private static InventoryMovementCommand Normalize(InventoryMovementCommand command) => command with
    {
        Reference = NormalizeOptional(command.Reference),
        Notes = NormalizeOptional(command.Notes),
        ApprovedSharedAssignments = (command.ApprovedSharedAssignments ?? [])
            .Distinct().OrderBy(item => item.ProductId).ThenBy(item => item.LocationId).ToArray()
    };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> ValidateStructure(InventoryMovementCommand command)
    {
        var errors = new List<string>();
        if (command.OperationId == Guid.Empty)
            errors.Add("El identificador de operación es obligatorio.");
        if (command.Lines.Count == 0)
            errors.Add("El movimiento debe contener al menos una línea.");
        if (command.Reference?.Length > 120)
            errors.Add("La referencia no puede superar 120 caracteres.");
        if (command.Notes?.Length > 500)
            errors.Add("Las observaciones no pueden superar 500 caracteres.");

        foreach (var (line, index) in command.Lines.Select((line, index) => (line, index)))
        {
            var label = $"Línea {index + 1}";
            if (line.ProductId == Guid.Empty)
                errors.Add($"{label}: el producto es obligatorio.");
            if (decimal.Round(line.Quantity, 4) != line.Quantity || Math.Abs(line.Quantity) > MaximumQuantity)
                errors.Add($"{label}: la cantidad excede la precisión numeric(18,4).");

            switch (command.Type)
            {
                case InventoryMovementType.Entry:
                    if (line.Quantity <= 0 || line.DestinationLocationId is null ||
                        line.SourceLocationId is not null || line.LocationId is not null)
                        errors.Add($"{label}: una entrada requiere cantidad positiva y únicamente ubicación destino.");
                    break;
                case InventoryMovementType.Exit:
                    if (line.Quantity <= 0 || line.SourceLocationId is null ||
                        line.DestinationLocationId is not null || line.LocationId is not null)
                        errors.Add($"{label}: una salida requiere cantidad positiva y únicamente ubicación origen.");
                    break;
                case InventoryMovementType.Transfer:
                    if (line.Quantity <= 0 || line.SourceLocationId is null || line.DestinationLocationId is null ||
                        line.LocationId is not null || line.SourceLocationId == line.DestinationLocationId)
                        errors.Add($"{label}: una transferencia requiere cantidad positiva y ubicaciones distintas.");
                    break;
                case InventoryMovementType.Adjustment:
                    if (line.LocationId is null || line.SourceLocationId is not null ||
                        line.DestinationLocationId is not null || line.ExpectedBalanceVersion is null)
                        errors.Add($"{label}: un ajuste requiere ubicación y versión del saldo consultado.");
                    break;
                default:
                    errors.Add($"{label}: tipo de movimiento no soportado.");
                    break;
            }
        }

        return errors;
    }

    private static List<string> ValidateProductsAndQuantities(
        InventoryMovementCommand command,
        IReadOnlyDictionary<Guid, Product> products)
    {
        var errors = new List<string>();
        foreach (var (line, index) in command.Lines.Select((line, index) => (line, index)))
        {
            if (!products.TryGetValue(line.ProductId, out var product))
            {
                errors.Add($"Línea {index + 1}: el producto no existe.");
                continue;
            }
            if (!product.IsActive)
                errors.Add($"Línea {index + 1}: el producto está inactivo.");
            if (!product.BaseUnit.IsActive)
                errors.Add($"Línea {index + 1}: la unidad base está inactiva.");
            if (!product.BaseUnit.AllowsDecimals && decimal.Truncate(line.Quantity) != line.Quantity)
                errors.Add($"Línea {index + 1}: la unidad base no permite cantidades decimales.");
        }
        return errors;
    }

    private static List<string> ValidateLocations(
        IReadOnlyCollection<Guid> requestedIds,
        IReadOnlyDictionary<Guid, Location> locations)
    {
        var errors = new List<string>();
        foreach (var id in requestedIds)
        {
            if (!locations.TryGetValue(id, out var location))
                errors.Add("Una ubicación indicada no existe.");
            else if (!location.IsActive)
                errors.Add($"La ubicación {location.Code} está inactiva.");
            else if (location.IsBlocked)
                errors.Add($"La ubicación {location.Code} está bloqueada.");
        }
        return errors;
    }

    private async Task<List<SharedLocationConflict>> FindSharingConflictsAsync(
        IReadOnlyCollection<AssignmentKey> pairs,
        IReadOnlyDictionary<Guid, Product> products,
        IReadOnlyDictionary<Guid, Location> locations,
        IReadOnlyCollection<SharedAssignmentApproval> approvals,
        CancellationToken cancellationToken)
    {
        var locationIds = pairs.Select(pair => pair.LocationId).Distinct().ToArray();
        var assignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Include(assignment => assignment.Product)
            .Where(assignment => locationIds.Contains(assignment.LocationId))
            .ToListAsync(cancellationToken);
        var occupiedBalances = await dbContext.InventoryBalances.AsNoTracking()
            .Include(balance => balance.Product)
            .Where(balance => locationIds.Contains(balance.LocationId) && balance.Quantity != 0)
            .ToListAsync(cancellationToken);
        var approved = approvals.Select(item => new AssignmentKey(item.ProductId, item.LocationId)).ToHashSet();
        var conflicts = new List<SharedLocationConflict>();

        foreach (var pair in pairs)
        {
            var sameAssignmentExists = assignments.Any(assignment =>
                assignment.ProductId == pair.ProductId && assignment.LocationId == pair.LocationId);
            var sameProductHasStock = occupiedBalances.Any(balance =>
                balance.ProductId == pair.ProductId && balance.LocationId == pair.LocationId);
            if (sameAssignmentExists || sameProductHasStock || approved.Contains(pair))
                continue;

            var otherSkus = assignments
                .Where(assignment => assignment.LocationId == pair.LocationId && assignment.IsActive &&
                    assignment.ProductId != pair.ProductId)
                .Select(assignment => assignment.Product.Sku)
                .Concat(occupiedBalances.Where(balance => balance.LocationId == pair.LocationId &&
                        balance.ProductId != pair.ProductId)
                    .Select(balance => balance.Product.Sku))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (otherSkus.Length == 0)
                continue;

            conflicts.Add(new(
                pair.ProductId,
                products[pair.ProductId].Sku,
                pair.LocationId,
                locations[pair.LocationId].Code,
                otherSkus));
        }

        return conflicts;
    }

    private async Task<HashSet<BalanceKey>> EnsureBalancesExistAsync(
        IReadOnlyCollection<BalanceKey> keys,
        CancellationToken cancellationToken)
    {
        var created = new HashSet<BalanceKey>();
        if (dbContext.Database.IsRelational())
        {
            foreach (var key in keys)
            {
                var id = Guid.NewGuid();
                var now = timeProvider.GetUtcNow();
                var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO inventory_balances (id, product_id, location_id, lot_id, quantity, updated_at)
                    VALUES ({{id}}, {{key.ProductId}}, {{key.LocationId}}, {{key.LotId}}, 0, {{now}})
                    ON CONFLICT DO NOTHING
                    """, cancellationToken);
                if (inserted == 1)
                    created.Add(key);
            }
            return created;
        }

        foreach (var key in keys)
        {
            if (!await dbContext.InventoryBalances.AnyAsync(balance =>
                    balance.ProductId == key.ProductId && balance.LocationId == key.LocationId &&
                    balance.LotId == key.LotId, cancellationToken))
            {
                created.Add(key);
                dbContext.InventoryBalances.Add(new InventoryBalance
                {
                    ProductId = key.ProductId,
                    LocationId = key.LocationId,
                    LotId = key.LotId,
                    Quantity = 0m,
                    UpdatedAt = timeProvider.GetUtcNow()
                });
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return created;
    }

    private async Task UpsertAssignmentsAsync(
        IReadOnlyCollection<AssignmentKey> pairs,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational())
        {
            foreach (var pair in pairs)
            {
                var now = timeProvider.GetUtcNow();
                await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO product_location_assignments (product_id, location_id, is_active, created_at, updated_at)
                    VALUES ({{pair.ProductId}}, {{pair.LocationId}}, TRUE, {{now}}, {{now}})
                    ON CONFLICT (product_id, location_id) DO UPDATE
                    SET is_active = TRUE,
                        updated_at = CASE
                            WHEN product_location_assignments.is_active = FALSE THEN EXCLUDED.updated_at
                            ELSE product_location_assignments.updated_at
                        END
                    """, cancellationToken);
            }
            return;
        }

        foreach (var pair in pairs)
        {
            var assignment = await dbContext.ProductLocationAssignments.FindAsync(
                [pair.ProductId, pair.LocationId], cancellationToken);
            if (assignment is null)
            {
                dbContext.ProductLocationAssignments.Add(new ProductLocationAssignment
                {
                    ProductId = pair.ProductId,
                    LocationId = pair.LocationId
                });
            }
            else if (!assignment.IsActive)
            {
                assignment.IsActive = true;
                assignment.UpdatedAt = timeProvider.GetUtcNow();
            }
        }
    }

    private static void ApplyLine(
        InventoryMovementType type,
        InventoryMovementLineCommand command,
        InventoryMovementLine line,
        IReadOnlyDictionary<BalanceKey, InventoryBalance> balances,
        DateTimeOffset now)
    {
        switch (type)
        {
            case InventoryMovementType.Entry:
                ApplyChange(line, balances[new(command.ProductId, command.DestinationLocationId!.Value, null)], command.Quantity, now);
                break;
            case InventoryMovementType.Exit:
                ApplyChange(line, balances[new(command.ProductId, command.SourceLocationId!.Value, null)], -command.Quantity, now);
                break;
            case InventoryMovementType.Transfer:
                ApplyChange(line, balances[new(command.ProductId, command.SourceLocationId!.Value, null)], -command.Quantity, now);
                ApplyChange(line, balances[new(command.ProductId, command.DestinationLocationId!.Value, null)], command.Quantity, now);
                break;
            case InventoryMovementType.Adjustment:
                var balance = balances[new(command.ProductId, command.LocationId!.Value, null)];
                line.PreviousQuantity = balance.Quantity;
                line.AdjustmentDelta = command.Quantity - balance.Quantity;
                ApplyChange(line, balance, line.AdjustmentDelta.Value, now);
                break;
            default:
                throw new InvalidOperationException("Tipo de movimiento no soportado.");
        }
    }

    private static void ApplyChange(
        InventoryMovementLine line,
        InventoryBalance balance,
        decimal delta,
        DateTimeOffset now)
        => ApplyChange(line, balance, delta, now, null);

    private static void ApplyChange(
        InventoryMovementLine line,
        InventoryBalance balance,
        decimal delta,
        DateTimeOffset now,
        ProductLot? lot)
    {
        var previous = balance.Quantity;
        var resulting = previous + delta;
        if (Math.Abs(resulting) > MaximumQuantity || decimal.Round(resulting, 4) != resulting)
            throw new InventoryQuantityOutOfRangeException(
                "El saldo resultante excede la precisión numeric(18,4).");

        line.BalanceChanges.Add(new InventoryBalanceChange
        {
            LocationId = balance.LocationId,
            LotId = balance.LotId,
            LotNumberSnapshot = lot?.Number,
            LotDateSnapshot = lot?.LotDate,
            DeltaQuantity = delta,
            PreviousQuantity = previous,
            ResultingQuantity = resulting
        });
        balance.Quantity = resulting;
        balance.UpdatedAt = now;
    }

    private static List<BalanceKey> GetBalanceKeys(InventoryMovementCommand command)
    {
        var keys = new List<BalanceKey>();
        foreach (var line in command.Lines)
        {
            switch (command.Type)
            {
                case InventoryMovementType.Entry:
                    keys.Add(new(line.ProductId, line.DestinationLocationId!.Value, null));
                    break;
                case InventoryMovementType.Exit:
                    keys.Add(new(line.ProductId, line.SourceLocationId!.Value, null));
                    break;
                case InventoryMovementType.Transfer:
                    keys.Add(new(line.ProductId, line.SourceLocationId!.Value, null));
                    keys.Add(new(line.ProductId, line.DestinationLocationId!.Value, null));
                    break;
                case InventoryMovementType.Adjustment:
                    keys.Add(new(line.ProductId, line.LocationId!.Value, null));
                    break;
            }
        }
        return keys;
    }

    private async Task LockBalancesAsync(
        IReadOnlyCollection<BalanceKey> keys,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var productIds = keys.Select(key => key.ProductId).Distinct().Order().ToArray();
        var locationIds = keys.Select(key => key.LocationId).Distinct().Order().ToArray();
        await using var command = CreateCommand(transaction, """
            SELECT id
            FROM inventory_balances
            WHERE product_id = ANY (@product_ids)
              AND location_id = ANY (@location_ids)
            ORDER BY product_id, location_id
            FOR UPDATE
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("product_ids", productIds)
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("location_ids", locationIds)
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid
        });
        await DrainAsync(command, cancellationToken);
    }

    private static async Task LockRowsAsync(
        string tableName,
        Guid[] ids,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
            return;
        if (tableName != "locations")
            throw new ArgumentOutOfRangeException(nameof(tableName));

        await using var command = CreateCommand(transaction, $"""
            SELECT id FROM {tableName}
            WHERE id = ANY (@ids)
            ORDER BY id
            FOR UPDATE
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", ids)
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid
        });
        await DrainAsync(command, cancellationToken);
    }

    private static DbCommand CreateCommand(IDbContextTransaction transaction, string sql)
    {
        var command = transaction.GetDbTransaction().Connection!.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = sql;
        return command;
    }

    private static async Task DrainAsync(DbCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { }
    }

    private async Task<InventoryMovementResult?> GetExistingResultAsync(
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var movement = await dbContext.InventoryMovements.AsNoTracking()
            .Include(item => item.ResponsibleUser)
            .Include(item => item.Lines)
                .ThenInclude(line => line.BalanceChanges)
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (movement is null)
            return null;
        if (!string.Equals(movement.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            return new(InventoryMovementStatus.IdempotencyConflict);

        var keys = movement.Lines.SelectMany(line => line.BalanceChanges.Select(change =>
            new BalanceKey(line.ProductId, change.LocationId, change.LotId))).Distinct().ToArray();
        var currentBalances = keys.Length == 0
            ? []
            : await LoadBalanceResultsAsync(keys, cancellationToken);
        return new(
            InventoryMovementStatus.Success,
            movement.Id,
            movement.ResponsibleUserId,
            movement.ResponsibleUser.FullName,
            currentBalances);
    }

    private async Task<IReadOnlyList<InventoryBalanceResult>> LoadBalanceResultsAsync(
        IReadOnlyCollection<BalanceKey> keys,
        CancellationToken cancellationToken)
    {
        var productIds = keys.Select(key => key.ProductId).Distinct().ToArray();
        var locationIds = keys.Select(key => key.LocationId).Distinct().ToArray();
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => productIds.Contains(balance.ProductId) &&
                locationIds.Contains(balance.LocationId))
            .Select(balance => new InventoryBalanceResult(
                balance.ProductId,
                balance.LocationId,
                balance.LotId,
                balance.Quantity,
                balance.Version,
                balance.Quantity < 0))
            .ToListAsync(cancellationToken);
        return balances.Where(balance => keys.Contains(new(
            balance.ProductId,
            balance.LocationId,
            balance.LotId))).ToArray();
    }

    private static IReadOnlyList<InventoryBalanceResult> ToBalanceResults(
        IReadOnlyCollection<BalanceKey> keys,
        IReadOnlyDictionary<BalanceKey, InventoryBalance> balances) =>
        keys.Distinct().OrderBy(key => key.ProductId).ThenBy(key => key.LocationId)
            .Select(key =>
            {
                var balance = balances[key];
                return new InventoryBalanceResult(
                    balance.ProductId,
                    balance.LocationId,
                    balance.LotId,
                    balance.Quantity,
                    balance.Version,
                    balance.Quantity < 0);
            }).ToArray();

    private static string CreateFingerprint(InventoryMovementCommand command, Guid userId)
    {
        var builder = new StringBuilder();
        builder.Append(userId.ToString("N")).Append('|')
            .Append(command.Type).Append('|')
            .Append(command.Reference ?? string.Empty).Append('|')
            .Append(command.Notes ?? string.Empty);
        foreach (var line in command.Lines)
        {
            builder.Append("|L:").Append(line.ProductId.ToString("N"))
                .Append(':').Append(line.Quantity.ToString("G29", CultureInfo.InvariantCulture))
                .Append(':').Append(line.SourceLocationId?.ToString("N") ?? string.Empty)
                .Append(':').Append(line.DestinationLocationId?.ToString("N") ?? string.Empty)
                .Append(':').Append(line.LocationId?.ToString("N") ?? string.Empty)
                .Append(':').Append(line.ExpectedBalanceVersion?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }
        foreach (var approval in command.ApprovedSharedAssignments ?? [])
        {
            builder.Append("|A:").Append(approval.ProductId.ToString("N"))
                .Append(':').Append(approval.LocationId.ToString("N"));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
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

    private static bool IsOperationIdConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException &&
        postgresException.SqlState == PostgresErrorCodes.UniqueViolation &&
        string.Equals(
            postgresException.ConstraintName,
            "IX_inventory_movements_operation_id",
            StringComparison.OrdinalIgnoreCase);

    private readonly record struct BalanceKey(Guid ProductId, Guid LocationId, Guid? LotId);
    private readonly record struct AssignmentKey(Guid ProductId, Guid LocationId);
    private sealed class InventoryQuantityOutOfRangeException(string message) : Exception(message);
}
