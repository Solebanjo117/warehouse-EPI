using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record PalletActivationCommand(Guid OperationId, Guid MovementId, Guid LocationId, decimal Quantity, string Pin);
public sealed record PalletIdentificationCommand(Guid OperationId, Guid LocationId, Guid ProductId, decimal Quantity,
    uint ExpectedBalanceVersion, decimal ExpectedIdentifiable);
public sealed record PalletVoidCommand(Guid OperationId, Guid PlateId, long ExpectedVersion, string Reason, string Pin);
public sealed record PalletIdentificationProduct(Guid ProductId, string Sku, string? Description, string UnitCode,
    bool AllowsDecimals, decimal Total, decimal Plated, decimal Unplated, decimal Protected, decimal Identifiable,
    uint BalanceVersion);
public sealed record PalletIdentificationSuggestion(Guid LocationId, string LocationCode, Guid ProductId);
public sealed record PalletStockProduct(Guid Id, string Sku, string? Description, string UnitCode, decimal Quantity);
public sealed record PalletStockLocation(Guid Id, string Code, string? Description, decimal Quantity);
public sealed record PalletConsolidationCommand(Guid OperationId, Guid ProductId, Guid LocationId);
public sealed record PalletQueryRow(Guid Id, string Identifier, Guid ProductId, string Sku, Guid LocationId, string Location,
    decimal Quantity, long Version, string Status, Guid? OriginMovementId);
public sealed record PalletAvailability(IReadOnlyList<PalletQueryRow> Plates, decimal Unplated);
public sealed record PalletHistoryRow(PalletPlateEvent Event, decimal BeforeQuantity, decimal AfterQuantity,
    string BeforeLocation, string AfterLocation, string Responsible, IReadOnlyList<Guid> RelatedPlates, string? Reason = null);
public sealed record PalletOrderLink(Guid Id, string Number);

public sealed class PalletTrackingService(WarehouseDbContext db, UserPinService pins, TimeProvider timeProvider)
{
    private sealed record IdentificationState(IReadOnlyList<InventoryBalance> Balances,
        IReadOnlyList<ProductLot> Lots, Dictionary<Guid, decimal> FreeLots, PalletIdentificationProduct Summary);

    public async Task<IReadOnlyList<PalletOrderLink>> OrdersAsync(Guid plateId, CancellationToken token = default)
    {
        var textId = plateId.ToString();
        var movementIds = await db.PalletPlateEvents.Where(x => x.PlateId == plateId && x.MovementId != null).Select(x => x.MovementId!.Value).Distinct().ToListAsync(token);
        var ids = await db.ProductionMaterialIssueLinks.Where(x => x.PlateAllocationsJson.Contains(textId) ||
                (x.InventoryMovementLineId != null && movementIds.Contains(x.InventoryMovementLine!.MovementId)))
            .Select(x => x.WorkOrderId).ToListAsync(token);
        ids.AddRange(await db.ProductionEvents.Where(x => x.InventoryMovementId != null && movementIds.Contains(x.InventoryMovementId.Value)).Select(x => x.WorkOrderId).ToListAsync(token));
        return await db.ProductionWorkOrders.Where(x => ids.Contains(x.Id)).OrderBy(x => x.Number).Select(x => new PalletOrderLink(x.Id, x.Number)).ToListAsync(token);
    }
    public async Task<IReadOnlyList<Location>> LocationsAsync(CancellationToken token = default) =>
        await db.Locations.AsNoTracking().Where(x => x.IsActive && !x.IsBlocked && x.IsPhysicallyPresent).OrderBy(x => x.Code).ToListAsync(token);

    public async Task<IReadOnlyList<PalletIdentificationProduct>> IdentificationProductsAsync(Guid locationId, CancellationToken token = default)
    {
        var productIds = await db.InventoryBalances.AsNoTracking()
            .Where(x => x.LocationId == locationId && x.Product.IsActive)
            .GroupBy(x => x.ProductId)
            .Where(x => x.Sum(y => y.Quantity) > 0)
            .Select(x => x.Key).ToListAsync(token);
        var result = new List<PalletIdentificationProduct>();
        foreach (var productId in productIds)
            result.Add((await IdentificationStateAsync(productId, locationId, token)).Summary);
        return result.OrderBy(x => x.Sku, StringComparer.Ordinal).ToArray();
    }

    public async Task<PalletIdentificationSuggestion?> SuggestionAsync(Guid movementId, CancellationToken token = default)
    {
        var linkedPlate = await db.PalletPlateEvents.AsNoTracking()
            .Where(x => x.MovementId == movementId)
            .OrderBy(x => x.Id)
            .Select(x => new PalletIdentificationSuggestion(x.Plate.LocationId, x.Plate.Location.Code, x.Plate.ProductId))
            .FirstOrDefaultAsync(token);
        if (linkedPlate is not null) return linkedPlate;
        var direct = await db.InventoryMovementLines.AsNoTracking()
            .Where(x => x.MovementId == movementId && (x.DestinationLocationId != null || x.SourceLocationId != null))
            .OrderBy(x => x.Id)
            .Select(x => new PalletIdentificationSuggestion(
                x.DestinationLocationId ?? x.SourceLocationId!.Value,
                x.DestinationLocationId != null ? x.DestinationLocation!.Code : x.SourceLocation!.Code,
                x.ProductId))
            .FirstOrDefaultAsync(token);
        if (direct is not null) return direct;
        return await db.InventoryBalanceChanges.AsNoTracking()
            .Where(x => x.MovementLine.MovementId == movementId)
            .OrderBy(x => x.MovementLine.LineNumber).ThenBy(x => x.Id)
            .Select(x => new PalletIdentificationSuggestion(x.LocationId, x.Location.Code, x.MovementLine.ProductId))
            .FirstOrDefaultAsync(token);
    }

    public async Task<IReadOnlyList<PalletStockProduct>> StockProductsAsync(
        Guid? locationId = null,
        string? search = null,
        CancellationToken token = default)
    {
        var term = search?.Trim().ToUpperInvariant();
        var query = db.InventoryBalances.AsNoTracking().Where(x => x.Product.IsActive && x.Product.BaseUnit.IsActive &&
            x.Location.IsActive && !x.Location.IsBlocked && x.Location.IsPhysicallyPresent);
        if (locationId.HasValue) query = query.Where(x => x.LocationId == locationId.Value);
        if (!string.IsNullOrWhiteSpace(term)) query = query.Where(x => x.Product.Sku.Contains(term) ||
            (x.Product.Description != null && x.Product.Description.ToUpper().Contains(term)) ||
            (x.Product.ExternalReference != null && x.Product.ExternalReference.ToUpper().Contains(term)));
        return await query.GroupBy(x => new { x.ProductId, x.Product.Sku, x.Product.Description, UnitCode = x.Product.BaseUnit.Code })
            .Where(x => x.Sum(y => y.Quantity) > 0).OrderBy(x => x.Key.Sku).Take(12)
            .Select(x => new PalletStockProduct(x.Key.ProductId, x.Key.Sku, x.Key.Description, x.Key.UnitCode, x.Sum(y => y.Quantity)))
            .ToListAsync(token);
    }

    public async Task<IReadOnlyList<PalletStockLocation>> StockLocationsAsync(
        Guid? productId = null,
        string? search = null,
        CancellationToken token = default)
    {
        var term = search?.Trim().ToUpperInvariant();
        var query = db.InventoryBalances.AsNoTracking().Where(x => x.Product.IsActive && x.Product.BaseUnit.IsActive &&
            x.Location.IsActive && !x.Location.IsBlocked && x.Location.IsPhysicallyPresent);
        if (productId.HasValue) query = query.Where(x => x.ProductId == productId.Value);
        if (!string.IsNullOrWhiteSpace(term)) query = query.Where(x => x.Location.Code.Contains(term) ||
            (x.Location.Description != null && x.Location.Description.ToUpper().Contains(term)));
        return await query.GroupBy(x => new { x.LocationId, x.Location.Code, x.Location.Description })
            .Where(x => x.Sum(y => y.Quantity) > 0).OrderBy(x => x.Key.Code).Take(12)
            .Select(x => new PalletStockLocation(x.Key.LocationId, x.Key.Code, x.Key.Description, x.Sum(y => y.Quantity)))
            .ToListAsync(token);
    }

    public async Task<InventoryMovementResult> IdentifyAsync(PalletIdentificationCommand command, CancellationToken token = default)
    {
        var fingerprint = Fingerprint(command);
        var prior = await db.PalletPlateEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "Identification", token);
        if (prior is not null) return prior.Fingerprint == fingerprint
            ? new(InventoryMovementStatus.Success, Plates: [await ExistingResultAsync(prior.PlateId, token)])
            : new(InventoryMovementStatus.IdempotencyConflict);
        if (command.OperationId == Guid.Empty || command.LocationId == Guid.Empty || command.ProductId == Guid.Empty ||
            command.Quantity <= 0 || decimal.Round(command.Quantity, 4) != command.Quantity || command.Quantity > InventoryMovementRules.MaximumQuantity)
            return Invalid("La identificación requiere ubicación, producto y una cantidad positiva válida.");

        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            if (tx is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({command.OperationId.ToString()}, 0))", token);
                await InventoryMovementStore.LockLocationsAsync([command.LocationId], tx, token);
            }
            prior = await db.PalletPlateEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "Identification", token);
            if (prior is not null) return prior.Fingerprint == fingerprint
                ? new(InventoryMovementStatus.Success, Plates: [await ExistingResultAsync(prior.PlateId, token)])
                : new(InventoryMovementStatus.IdempotencyConflict);
            var location = await db.Locations.SingleOrDefaultAsync(x => x.Id == command.LocationId, token);
            if (location is null || !location.IsOperational) return Invalid("Selecciona una ubicación activa, físicamente presente y no bloqueada.");
            var product = await db.Products.Include(x => x.BaseUnit).SingleOrDefaultAsync(x => x.Id == command.ProductId && x.IsActive, token);
            if (product is null) return Invalid("Selecciona un producto activo con existencia positiva en la ubicación.");
            if (!product.BaseUnit.AllowsDecimals && decimal.Truncate(command.Quantity) != command.Quantity)
                return Invalid("La unidad del producto no permite cantidades decimales.");
            var state = await IdentificationStateAsync(command.ProductId, command.LocationId, token);
            if (state.Summary.Total <= 0) return Invalid("El producto ya no tiene existencia positiva en la ubicación.");
            if (state.Summary.BalanceVersion != command.ExpectedBalanceVersion || state.Summary.Identifiable != command.ExpectedIdentifiable)
                return new(InventoryMovementStatus.BalanceChanged, Errors: ["El saldo cambió. Consulta nuevamente antes de identificar el pallet."]);
            if (command.Quantity > state.Summary.Identifiable || state.FreeLots.Count == 0)
                return Invalid("El saldo libre sin placa no cubre la cantidad. Revisa reservas, preparaciones y asignaciones de producción.");

            var now = timeProvider.GetUtcNow();
            var active = await db.PalletPlates.Include(x => x.Lots)
                .Where(x => x.ProductId == command.ProductId && x.LocationId == command.LocationId && !x.IsVoided && x.Quantity > 0)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token);
            var plate = active.FirstOrDefault();
            var isNew = plate is null;
            plate ??= new PalletPlate { ProductId = command.ProductId, LocationId = command.LocationId, CreatedAt = now, IsVoided = true };
            var before = PalletPlateEngine.State(plate);
            plate.IsVoided = false;
            var engine = new PalletPlateEngine(db);
            foreach (var secondary in active.Skip(1))
                if (await HasPlateAssignmentsAsync(secondary.Id, token))
                    return Invalid("Hay placas duplicadas con material reservado o preparado. Concilia esas reservas antes de imprimir.");

            foreach (var secondary in active.Skip(1))
            {
                var secondaryBefore = PalletPlateEngine.State(secondary);
                foreach (var lot in secondary.Lots.Where(x => x.Quantity != 0).ToArray())
                {
                    PalletPlateEngine.Change(plate, lot.LotId, lot.Quantity);
                    PalletPlateEngine.Change(secondary, lot.LotId, -lot.Quantity);
                }
                engine.Record(secondary, secondaryBefore, command.OperationId, null, now, "Consolidation", fingerprint: fingerprint);
            }
            foreach (var allocation in PalletPlateEngine.Allocate(command.Quantity, state.Lots,
                         id => state.FreeLots.GetValueOrDefault(id), state.FreeLots.Keys.First()))
                PalletPlateEngine.Change(plate, allocation.LotId, allocation.Quantity);
            if (isNew) db.PalletPlates.Add(plate);
            engine.Record(plate, before, command.OperationId, null, now, "Identification", fingerprint: fingerprint);
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(InventoryMovementStatus.Success, Plates: [PalletPlateEngine.Result(plate)]);
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            var existing = await db.PalletPlateEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "Identification", token);
            return existing?.Fingerprint == fingerprint
                ? new(InventoryMovementStatus.Success, Plates: [await ExistingResultAsync(existing.PlateId, token)])
                : Invalid("La ubicación, las placas o el saldo cambiaron. Consulta nuevamente antes de identificar.");
        }
    }

    public async Task<InventoryMovementResult> ConsolidateAsync(PalletConsolidationCommand command, CancellationToken token = default)
    {
        var fingerprint = Fingerprint(command);
        var prior = await db.PalletPlateEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "ConsolidationPrimary", token);
        if (prior is not null) return prior.Fingerprint == fingerprint
            ? new(InventoryMovementStatus.Success, Plates: [await ExistingResultAsync(prior.PlateId, token)])
            : new(InventoryMovementStatus.IdempotencyConflict);
        if (command.OperationId == Guid.Empty || command.ProductId == Guid.Empty || command.LocationId == Guid.Empty)
            return Invalid("Selecciona un producto y una ubicación con stock antes de imprimir.");

        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            if (tx is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({command.OperationId.ToString()}, 0))", token);
                await InventoryMovementStore.LockLocationsAsync([command.LocationId], tx, token);
            }
            prior = await db.PalletPlateEvents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "ConsolidationPrimary", token);
            if (prior is not null) return prior.Fingerprint == fingerprint
                ? new(InventoryMovementStatus.Success, Plates: [await ExistingResultAsync(prior.PlateId, token)])
                : new(InventoryMovementStatus.IdempotencyConflict);
            var active = await db.PalletPlates.Include(x => x.Lots)
                .Where(x => x.ProductId == command.ProductId && x.LocationId == command.LocationId && !x.IsVoided && x.Quantity > 0)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token);
            if (active.Count == 0) return Invalid("No existe una placa vigente para ese producto y ubicación.");
            foreach (var secondary in active.Skip(1))
                if (await HasPlateAssignmentsAsync(secondary.Id, token))
                    return Invalid("Hay placas duplicadas con material reservado o preparado. Concilia esas reservas antes de imprimir.");

            var now = timeProvider.GetUtcNow();
            var engine = new PalletPlateEngine(db);
            var canonical = active[0];
            var primaryBefore = PalletPlateEngine.State(canonical);
            foreach (var secondary in active.Skip(1))
            {
                var secondaryBefore = PalletPlateEngine.State(secondary);
                foreach (var lot in secondary.Lots.Where(x => x.Quantity != 0).ToArray())
                {
                    PalletPlateEngine.Change(canonical, lot.LotId, lot.Quantity);
                    PalletPlateEngine.Change(secondary, lot.LotId, -lot.Quantity);
                }
                engine.Record(secondary, secondaryBefore, command.OperationId, null, now, "Consolidation", fingerprint: fingerprint);
            }
            engine.Record(canonical, primaryBefore, command.OperationId, null, now, "ConsolidationPrimary", fingerprint: fingerprint);
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(InventoryMovementStatus.Success, Plates: [PalletPlateEngine.Result(canonical)]);
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            var existing = await db.PalletPlateEvents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "ConsolidationPrimary", token);
            return existing?.Fingerprint == fingerprint
                ? new(InventoryMovementStatus.Success, Plates: [await ExistingResultAsync(existing.PlateId, token)])
                : Invalid("La ubicación o las placas cambiaron. Consulta nuevamente antes de imprimir.");
        }
    }

    public async Task<InventoryMovementResult> VoidIdentificationAsync(PalletVoidCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code != "ADMIN") return new(InventoryMovementStatus.InvalidPin);
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (command.OperationId == Guid.Empty || command.PlateId == Guid.Empty || reason.Length is < 3 or > 500)
            return Invalid("La anulación requiere un motivo de 3 a 500 caracteres.");
        var fingerprint = Fingerprint(command with { Pin = user.Id.ToString(), Reason = reason });
        var prior = await db.PalletPlateEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "IdentificationVoided", token);
        if (prior is not null) return prior.Fingerprint == fingerprint ? new(InventoryMovementStatus.Success) : new(InventoryMovementStatus.IdempotencyConflict);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var plateLocation = await db.PalletPlates.AsNoTracking().Where(x => x.Id == command.PlateId).Select(x => (Guid?)x.LocationId).SingleOrDefaultAsync(token);
            if (!plateLocation.HasValue) return Invalid("No existe la placa indicada.");
            if (tx is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({command.OperationId.ToString()}, 0))", token);
                await InventoryMovementStore.LockLocationsAsync([plateLocation.Value], tx, token);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM pallet_plates WHERE id = {command.PlateId} FOR UPDATE", token);
            }
            var plate = await db.PalletPlates.Include(x => x.Lots).SingleAsync(x => x.Id == command.PlateId, token);
            var events = await db.PalletPlateEvents.Where(x => x.PlateId == command.PlateId).OrderBy(x => x.PlateVersion).ToListAsync(token);
            if (plate.IsVoided || plate.Version != command.ExpectedVersion || events.Count != 1 || events[0].Kind != "Identification" ||
                events[0].MovementId.HasValue || await HasPlateAssignmentsAsync(command.PlateId, token))
                return Invalid("La placa ya cambió o tiene movimientos, asignaciones o eventos posteriores; no puede anularse.");
            var before = PalletPlateEngine.State(plate); plate.IsVoided = true;
            new PalletPlateEngine(db).Record(plate, before, command.OperationId, user.Id, timeProvider.GetUtcNow(),
                "IdentificationVoided", reverses: events[0].Id, fingerprint: fingerprint);
            db.PalletPlateEvents.Local.Single(x => x.OperationId == command.OperationId && x.Kind == "IdentificationVoided").After =
                JsonSerializer.Serialize(PalletPlateEngine.State(plate) with { Reason = reason });
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(InventoryMovementStatus.Success);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            return Invalid("La placa cambió. Consulta nuevamente antes de anular.");
        }
    }
    public async Task<PalletAvailability> AvailableAsync(Guid productId, Guid locationId, bool blind = false, Guid? issueLinkId = null, CancellationToken token = default)
    {
        var plates = await db.PalletPlates.AsNoTracking().Include(x => x.Product).Include(x => x.Location)
            .Where(x => x.ProductId == productId && x.LocationId == locationId && !x.IsVoided).OrderBy(x => x.CreatedAt).ToListAsync(token);
        var total = blind ? 0 : await db.InventoryBalances.Where(x => x.ProductId == productId && x.LocationId == locationId).SumAsync(x => x.Quantity, token);
        if (issueLinkId.HasValue)
        {
            var issue = await db.ProductionMaterialIssueLinks.Include(x => x.OperationLines).ThenInclude(x => x.Operation)
                .SingleOrDefaultAsync(x => x.Id == issueLinkId && x.ProductId == productId && x.WipLocationId == locationId, token);
            if (issue is null) return new([], 0);
            var owned = await Production.ProductionPlateAllocation.RemainingAsync(db, issue, token);
            var reversed = await db.ProductionMaterialOperations.Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
            var remaining = issue.Quantity - issue.CancelledQuantity - issue.OperationLines.Where(x => x.Operation.Type != ProductionMaterialOperationType.Reversal && !reversed.Contains(x.Operation.Id)).Sum(x => x.Quantity);
            var rows = plates.Where(x => owned.Any(a => a.PlateId == x.Id)).Select(x => Row(x, blind) with { Quantity = blind ? 0 : owned.Where(a => a.PlateId == x.Id).Sum(a => a.Quantity) }).ToArray();
            return new(rows, blind ? 0 : remaining - owned.Sum(x => x.Quantity));
        }
        return new(plates.Select(x => Row(x, blind)).ToArray(), blind ? 0 : total - plates.Sum(x => x.Quantity));
    }

    public async Task<IReadOnlyList<PalletQueryRow>> SearchAsync(string? search = null, string? status = null, Guid? originId = null, int page = 1, CancellationToken token = default)
    {
        var query = db.PalletPlates.AsNoTracking().Include(x => x.Product).Include(x => x.Location).AsQueryable();
        if (originId.HasValue) query = query.Where(x => x.OriginMovementId == originId || db.PalletPlateEvents.Any(e => e.PlateId == x.Id && e.MovementId == originId));
        if (Guid.TryParse(search?.Replace("PLT-", "", StringComparison.OrdinalIgnoreCase), out var id))
        {
            var isPlate = await db.PalletPlates.AnyAsync(x => x.Id == id, token);
            query = isPlate ? query.Where(x => x.Id == id) : query.Where(x => x.OriginMovementId == id);
        }
        else if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Product.Sku.Contains(search) || x.Location.Code.Contains(search));
        query = status switch
        {
            "available" => query.Where(x => !x.IsVoided && x.Quantity > 0),
            "empty" => query.Where(x => !x.IsVoided && x.Quantity == 0),
            "negative" => query.Where(x => !x.IsVoided && x.Quantity < 0),
            "void" => query.Where(x => x.IsVoided),
            _ => query
        };
        return (await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((Math.Clamp(page, 1, 100000) - 1) * 50).Take(50).ToListAsync(token))
            .Select(x => Row(x)).ToArray();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PalletQueryRow>>> PrintablePlatesForMovementsAsync(
        IReadOnlyCollection<Guid> movementIds,
        CancellationToken token = default)
    {
        var ids = movementIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, IReadOnlyList<PalletQueryRow>>();

        var eventLinks = await db.PalletPlateEvents.AsNoTracking()
            .Where(x => x.MovementId.HasValue && ids.Contains(x.MovementId.Value))
            .Select(x => new { MovementId = x.MovementId!.Value, x.PlateId })
            .Distinct()
            .ToListAsync(token);
        var originLinks = await db.PalletPlates.AsNoTracking()
            .Where(x => x.OriginMovementId.HasValue && ids.Contains(x.OriginMovementId.Value))
            .Select(x => new { MovementId = x.OriginMovementId!.Value, PlateId = x.Id })
            .ToListAsync(token);
        var links = eventLinks.Concat(originLinks)
            .DistinctBy(x => (x.MovementId, x.PlateId))
            .ToArray();
        var plateIds = links.Select(x => x.PlateId).Distinct().ToArray();
        var plates = await db.PalletPlates.AsNoTracking().Include(x => x.Product).Include(x => x.Location)
            .Where(x => plateIds.Contains(x.Id) && !x.IsVoided)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(token);
        var rows = plates.ToDictionary(x => x.Id, x => Row(x));

        return links.Where(x => rows.ContainsKey(x.PlateId))
            .GroupBy(x => x.MovementId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var linked = group.Select(x => rows[x.PlateId]).DistinctBy(x => x.Id).ToArray();
                    return (IReadOnlyList<PalletQueryRow>)(linked.Any(x => x.Quantity > 0)
                        ? linked.Where(x => x.Quantity > 0).ToArray()
                        : linked);
                });
    }

    public async Task<IReadOnlyList<PalletPlateEvent>> HistoryAsync(Guid id, CancellationToken token = default) =>
        await db.PalletPlateEvents.AsNoTracking().Where(x => x.PlateId == id).OrderByDescending(x => x.PlateVersion).Take(200).ToListAsync(token);

    public async Task<IReadOnlyList<PalletHistoryRow>> HistoryDetailsAsync(Guid id, CancellationToken token = default)
    {
        var events = await HistoryAsync(id, token);
        var states = events.Select(e => (Event: e, Before: System.Text.Json.JsonSerializer.Deserialize<PalletState>(e.Before)!, After: System.Text.Json.JsonSerializer.Deserialize<PalletState>(e.After)!)).ToArray();
        var locationIds = states.SelectMany(x => new[] { x.Before.LocationId, x.After.LocationId }).Distinct().ToArray();
        var userIds = events.Where(x => x.ResponsibleUserId.HasValue).Select(x => x.ResponsibleUserId!.Value).Distinct().ToArray();
        var operationIds = events.Select(x => x.OperationId).Distinct().ToArray();
        var locations = await db.Locations.Where(x => locationIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, token);
        var users = await db.Users.Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName, token);
        var related = await db.PalletPlateEvents.Where(x => operationIds.Contains(x.OperationId) && x.PlateId != id).Select(x => new { x.OperationId, x.PlateId }).Distinct().ToListAsync(token);
        return states.Select(x => new PalletHistoryRow(x.Event, x.Before.Quantity, x.After.Quantity,
            locations.GetValueOrDefault(x.Before.LocationId, "—"), locations.GetValueOrDefault(x.After.LocationId, "—"),
            x.Event.ResponsibleUserId is Guid userId ? users.GetValueOrDefault(userId, "—") : "Sin identificación de operador",
            related.Where(e => e.OperationId == x.Event.OperationId).Select(e => e.PlateId).ToArray(), x.After.Reason)).ToArray();
    }

    private static PalletQueryRow Row(PalletPlate p, bool blind = false) => new(p.Id, p.Identifier, p.ProductId, p.Product.Sku, p.LocationId,
        p.Location.Code, blind ? 0 : p.Quantity, p.Version, blind ? "Por contar" : p.Status, p.OriginMovementId);

    public async Task<InventoryMovementResult> ActivateAsync(PalletActivationCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user is null || user.Role.Code is not ("ADMIN" or "OPERATOR")) return new(InventoryMovementStatus.InvalidPin);
        var fingerprint = Fingerprint(command with { Pin = user.Id.ToString() });
        var prior = await db.PalletPlateEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "Activation", token);
        if (prior is not null) return prior.Fingerprint == fingerprint ? new(InventoryMovementStatus.Success, command.MovementId) : new(InventoryMovementStatus.IdempotencyConflict);
        if (command.OperationId == Guid.Empty || command.Quantity <= 0 || decimal.Round(command.Quantity, 4) != command.Quantity || command.Quantity > InventoryMovementRules.MaximumQuantity)
            return Invalid("La activación requiere una cantidad positiva válida.");
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            if (tx is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({command.OperationId.ToString()}, 0))", token);
                await InventoryMovementStore.LockLocationsAsync([command.LocationId], tx, token);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM inventory_movements WHERE id = {command.MovementId} FOR UPDATE", token);
            }
            prior = await db.PalletPlateEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "Activation", token);
            if (prior is not null) return prior.Fingerprint == fingerprint ? new(InventoryMovementStatus.Success, command.MovementId) : new(InventoryMovementStatus.IdempotencyConflict);
            var movement = await db.InventoryMovements.Include(x => x.Lines).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
                .SingleOrDefaultAsync(x => x.Id == command.MovementId, token);
            if (movement is null || !Labels.PalletLicensePlateService.IsEligible(movement.Type, movement.Purpose, movement.Lines.Count)
                || await db.InventoryMovementCorrections.AnyAsync(x => x.OriginalMovementId == movement.Id || x.ReversalMovementId == movement.Id, token))
                return Invalid("La entrada no es elegible para activar su placa documental.");
            if (await db.PalletPlates.AnyAsync(x => x.Id == movement.Id || x.OriginMovementId == movement.Id, token)) return Invalid("Esta entrada ya tiene seguimiento por placa.");
            var source = movement.Lines.Single();
            if (!source.Product.BaseUnit.AllowsDecimals && decimal.Truncate(command.Quantity) != command.Quantity) return Invalid("La unidad no permite decimales.");
            var location = await db.Locations.SingleOrDefaultAsync(x => x.Id == command.LocationId, token);
            if (location is null || !location.IsOperational) return Invalid("Selecciona una ubicación operativa.");
            var balances = await db.InventoryBalances.Include(x => x.Lot).Where(x => x.ProductId == source.ProductId && x.LocationId == command.LocationId && x.LotId != null).ToListAsync(token);
            var assigned = await db.PalletPlateLots.Where(x => x.Plate.ProductId == source.ProductId && x.Plate.LocationId == command.LocationId && !x.Plate.IsVoided).ToListAsync(token);
            var free = balances.ToDictionary(x => x.LotId!.Value, x => x.Quantity - assigned.Where(a => a.LotId == x.LotId).Sum(a => a.Quantity));
            if (free.Values.Sum() < command.Quantity || free.Count == 0) return Invalid("El saldo sin placa no respalda la cantidad. Concilia el inventario antes de activar.");
            var plate = new PalletPlate
            {
                Id = movement.Id,
                OriginMovementId = movement.Id,
                ProductId = source.ProductId,
                LocationId = command.LocationId,
                CreatedAt = timeProvider.GetUtcNow(),
                IsVoided = true
            };
            var before = PalletPlateEngine.State(plate); plate.IsVoided = false;
            foreach (var a in PalletPlateEngine.Allocate(command.Quantity, balances.Select(x => x.Lot!), id => free.GetValueOrDefault(id), free.Keys.First()))
                PalletPlateEngine.Change(plate, a.LotId, a.Quantity);
            db.PalletPlates.Add(plate);
            new PalletPlateEngine(db).Record(plate, before, command.OperationId, user.Id, plate.CreatedAt, "Activation", movement, fingerprint: fingerprint);
            await db.SaveChangesAsync(token); if (tx is not null) await tx.CommitAsync(token);
            return new(InventoryMovementStatus.Success, movement.Id, Plates: [PalletPlateEngine.Result(plate)]);
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(token); db.ChangeTracker.Clear();
            var existing = await db.PalletPlateEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId && x.Kind == "Activation", token);
            return existing?.Fingerprint == fingerprint ? new(InventoryMovementStatus.Success, command.MovementId) : Invalid("La placa o el saldo cambió. Consulta nuevamente antes de activar.");
        }
    }

    private static InventoryMovementResult Invalid(string message) => new(InventoryMovementStatus.ValidationFailed, Errors: [message]);

    private async Task<IdentificationState> IdentificationStateAsync(Guid productId, Guid locationId, CancellationToken token)
    {
        var balances = await db.InventoryBalances.AsNoTracking().Include(x => x.Lot)
            .Where(x => x.ProductId == productId && x.LocationId == locationId && x.LotId != null).ToListAsync(token);
        var product = await db.Products.AsNoTracking().Include(x => x.BaseUnit).SingleAsync(x => x.Id == productId, token);
        var plateLots = await db.PalletPlateLots.AsNoTracking()
            .Where(x => x.Plate.ProductId == productId && x.Plate.LocationId == locationId && !x.Plate.IsVoided)
            .ToListAsync(token);
        var free = balances.ToDictionary(x => x.LotId!.Value,
            x => x.Quantity - plateLots.Where(y => y.LotId == x.LotId).Sum(y => y.Quantity));

        var reservations = await db.ProductionWarehouseReservations.AsNoTracking()
            .Where(x => x.SupplyRequestLine.ProductId == productId && x.LocationId == locationId && x.Quantity > x.ReleasedQuantity)
            .GroupBy(x => x.LotId).Select(x => new { x.Key, Quantity = x.Sum(y => y.Quantity - y.ReleasedQuantity) }).ToListAsync(token);
        foreach (var reserved in reservations) free[reserved.Key] = free.GetValueOrDefault(reserved.Key) - reserved.Quantity;

        var reversed = await db.ProductionMaterialOperations.AsNoTracking().Where(x => x.ReversesOperationId != null)
            .Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var issues = await db.ProductionMaterialIssueLinks.AsNoTracking().Include(x => x.Lots).ThenInclude(x => x.Lot)
            .Include(x => x.OperationLines).ThenInclude(x => x.Operation)
            .Include(x => x.OperationLines).ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
            .Where(x => x.ProductId == productId && x.WipLocationId == locationId && x.Quantity > x.CancelledQuantity).ToListAsync(token);
        foreach (var issue in issues)
        {
            var remaining = InventoryMovementService.RemainingLots(issue, reversed);
            var plated = await Production.ProductionPlateAllocation.RemainingAsync(db, issue, token);
            foreach (var lot in remaining)
            {
                var quantity = lot.Quantity - plated.Where(x => x.LotId == lot.LotId).Sum(x => x.Quantity);
                if (quantity > 0) free[lot.LotId] = free.GetValueOrDefault(lot.LotId) - quantity;
            }
        }

        var preparationSources = await db.ProductionSupplyPreparationSources.AsNoTracking()
            .Where(x => x.LocationId == locationId && x.Preparation.Status == ProductionSupplyPreparationStatus.Open &&
                x.Preparation.SupplyRequestLine.ProductId == productId)
            .Select(x => new { x.Kind, x.Quantity, x.PlatesJson }).ToListAsync(token);
        decimal PreparedUnplated(ProductionSupplySourceKind kind) => preparationSources.Where(x => x.Kind == kind).Sum(x => Math.Max(0, x.Quantity -
            (JsonSerializer.Deserialize<List<PalletSelection>>(x.PlatesJson) ?? []).Sum(y => y.Quantity)));
        var extraPrepared = Math.Max(0, PreparedUnplated(ProductionSupplySourceKind.Warehouse) - reservations.Sum(x => x.Quantity)) +
            PreparedUnplated(ProductionSupplySourceKind.ExistingWip);
        foreach (var lot in balances.Select(x => x.Lot!).OrderBy(x => x.LotDate is null).ThenBy(x => x.LotDate).ThenBy(x => x.CreatedAt).ThenBy(x => x.NormalizedNumber))
        {
            var take = Math.Min(extraPrepared, Math.Max(0, free.GetValueOrDefault(lot.Id)));
            free[lot.Id] = free.GetValueOrDefault(lot.Id) - take; extraPrepared -= take;
            if (extraPrepared == 0) break;
        }
        foreach (var key in free.Keys.ToArray()) free[key] = Math.Max(0, free[key]);
        var total = balances.Sum(x => x.Quantity);
        var platedTotal = plateLots.Sum(x => x.Quantity);
        var identifiable = free.Values.Sum();
        var protectedQuantity = Math.Max(0, total - platedTotal - identifiable);
        return new(balances, balances.Select(x => x.Lot!).ToArray(), free,
            new(product.Id, product.Sku, product.Description, product.BaseUnit.Code, product.BaseUnit.AllowsDecimals,
                total, platedTotal, total - platedTotal, protectedQuantity, identifiable, InventoryLotEngine.AggregateVersion(balances)));
    }

    private async Task<PalletMovementResult> ExistingResultAsync(Guid plateId, CancellationToken token)
    {
        var plate = await db.PalletPlates.AsNoTracking().SingleAsync(x => x.Id == plateId, token);
        return PalletPlateEngine.Result(plate);
    }

    private async Task<bool> HasPlateAssignmentsAsync(Guid plateId, CancellationToken token)
    {
        var text = plateId.ToString();
        return await db.ProductionMaterialIssueLinks.AsNoTracking().AnyAsync(x => x.PlateAllocationsJson.Contains(text), token) ||
            await db.ProductionSupplyPreparationSources.AsNoTracking().AnyAsync(x => x.PlatesJson.Contains(text), token);
    }

    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
