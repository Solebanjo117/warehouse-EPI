using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record StagingEntryRow(Guid MovementId, string Sku, string UnitCode, decimal Received,
    DateTimeOffset OccurredAt, string? Reference, string Responsible, bool NeedsIdentification,
    IReadOnlyList<PalletQueryRow> Plates);
public sealed record StagingEntryPage(IReadOnlyList<StagingEntryRow> Items, bool HasMore);
public sealed record StagingEntryCommand(Guid OperationId, Guid MovementId, Guid LocationId, decimal Quantity,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ArrivalLineId = null);

public sealed partial class PalletTrackingService
{
    public Task<bool> IsStagingAsync(Guid locationId, CancellationToken token = default) =>
        db.Locations.AnyAsync(x => x.Id == locationId && x.Kind == LocationKind.Area &&
            x.Code == "STAGING" && x.OperationalRole != LocationOperationalRole.Wip, token);

    private IQueryable<InventoryMovement> StagingEntries(Guid locationId) => db.InventoryMovements
        .Where(x => x.Type == InventoryMovementType.Entry &&
            (x.Purpose == InventoryMovementPurpose.Standard || x.Purpose == InventoryMovementPurpose.DocumentReceipt ||
                x.Purpose == InventoryMovementPurpose.ProductionReceipt) &&
            x.Lines.Count == 1 && x.Lines.Any(l => l.DestinationLocationId == locationId) &&
            !db.InventoryMovementCorrections.Any(c => c.OriginalMovementId == x.Id || c.ReversalMovementId == x.Id));

    public Task<bool> IsStagingEntryAsync(Guid movementId, CancellationToken token = default) =>
        db.InventoryMovements.AnyAsync(x => x.Id == movementId && x.Type == InventoryMovementType.Entry &&
            x.Lines.Any(l => l.DestinationLocation != null && l.DestinationLocation.Kind == LocationKind.Area &&
                l.DestinationLocation.Code == "STAGING" && l.DestinationLocation.OperationalRole != LocationOperationalRole.Wip), token);

    public async Task<StagingEntryPage> StagingEntriesAsync(Guid locationId, Guid? productId = null,
        int page = 1, Guid? movementId = null, CancellationToken token = default)
    {
        if (!await IsStagingAsync(locationId, token)) return new([], false);
        var query = StagingEntries(locationId).AsNoTracking();
        if (movementId.HasValue) query = query.Where(x => x.Id == movementId.Value);
        if (productId.HasValue) query = query.Where(x => x.Lines.Any(l => l.ProductId == productId));
        var entries = await query.Include(x => x.ResponsibleUser)
            .Include(x => x.Lines).ThenInclude(x => x.Product)
            .Include(x => x.Lines).ThenInclude(x => x.Unit)
            .OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Id)
            .Skip((Math.Clamp(page, 1, 100000) - 1) * 25).Take(26).ToListAsync(token);
        var ids = entries.Take(25).Select(x => x.Id).ToArray();
        var plates = await db.PalletPlates.AsNoTracking().Include(x => x.Product).Include(x => x.Location)
            .Where(x => x.OriginMovementId.HasValue && ids.Contains(x.OriginMovementId.Value)).ToListAsync(token);
        var plateIds = plates.Select(x => x.Id).ToArray();
        var events = await db.PalletPlateEvents.AsNoTracking().Where(x => plateIds.Contains(x.PlateId)).ToListAsync(token);
        return new(entries.Take(25).Select(entry =>
        {
            var line = entry.Lines.Single();
            var owned = plates.Where(x => x.OriginMovementId == entry.Id).ToArray();
            var recovered = owned.Any(p => events.Any(e => e.PlateId == p.Id && e.Kind == "StagingEntryIdentification"));
            var ambiguous = owned.Any(p => HasMixedHistory(events.Where(e => e.PlateId == p.Id)));
            var needsIdentification = !recovered && (owned.Length == 0 || ambiguous);
            return new StagingEntryRow(entry.Id, line.Product.Sku, line.Unit.Code, line.Quantity,
                entry.OccurredAt, entry.Reference, entry.ResponsibleUser.FullName, needsIdentification,
                needsIdentification ? [] : owned.Where(p => !p.IsVoided && p.LocationId == locationId && p.Quantity > 0 &&
                    !HasMixedHistory(events.Where(e => e.PlateId == p.Id)))
                    .Select(p => Row(p)).ToArray());
        }).ToArray(), entries.Count > 25);
    }

    // A plate that received unrelated stock is not evidence of the remaining quantity of its original entry.
    private static bool HasMixedHistory(IEnumerable<PalletPlateEvent> events) => events.Any(e =>
        e.Kind is "Consolidation" or "ConsolidationPrimary" or "Identification" or "Adjustment" ||
        (e.Kind == "Transfer" && e.PlateVersion > 1 &&
         System.Text.Json.JsonSerializer.Deserialize<PalletState>(e.After)!.Quantity >
         System.Text.Json.JsonSerializer.Deserialize<PalletState>(e.Before)!.Quantity));

    public async Task<InventoryMovementResult> IdentifyStagingEntryAsync(StagingEntryCommand command,
        CancellationToken token = default)
    {
        if (command.OperationId == Guid.Empty || command.MovementId == Guid.Empty || command.Quantity <= 0 ||
            command.Quantity > InventoryMovementRules.MaximumQuantity || decimal.Round(command.Quantity, 4) != command.Quantity)
            return Invalid("Confirma una cantidad física positiva para esta entrada.");
        if (!await IsStagingAsync(command.LocationId, token)) return Invalid("La impresión por entrada solo está disponible en el área STAGING.");
        var fingerprint = Fingerprint(command);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            if (tx is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({command.OperationId.ToString()}, 0))", token);
                await InventoryMovementStore.LockLocationsAsync([command.LocationId], tx, token);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM inventory_movements WHERE id = {command.MovementId} FOR UPDATE", token);
            }
            var prior = await db.PalletPlateEvents.AsNoTracking().SingleOrDefaultAsync(e =>
                e.OperationId == command.OperationId && e.Kind == "StagingEntryIdentification", token);
            if (prior is not null) return prior.Fingerprint == fingerprint
                ? new(InventoryMovementStatus.Success, Plates: [await ExistingResultAsync(prior.PlateId, token)])
                : new(InventoryMovementStatus.IdempotencyConflict);
            var location = await db.Locations.SingleAsync(x => x.Id == command.LocationId, token);
            if (!location.IsOperational || !await IsStagingAsync(location.Id, token)) return Invalid("Selecciona una ubicación operativa.");
            InventoryMovementLine? line;
            IReadOnlyList<PalletSelection> knownArrivalPlates = [];
            if (command.ArrivalLineId is Guid arrivalLineId)
            {
                var arrival = await new StagingArrivalQuery(db).GetAsync(arrivalLineId, token);
                if (arrival is null || arrival.MovementId != command.MovementId || arrival.LocationId != location.Id || !arrival.NeedsIdentification)
                    return Invalid("La llegada ya está identificada o no está vigente. Consulta nuevamente STAGING.");
                knownArrivalPlates = StagingArrivalQuery.DecodeVersion(arrival.Version) ?? [];
                if (command.Quantity < knownArrivalPlates.Sum(p => p.Quantity))
                    return Invalid("La cantidad física no puede ser menor que el material ya identificado de esta llegada.");
                line = await new StagingArrivalQuery(db).Arrivals.Where(l => l.Id == arrivalLineId)
                    .Include(l => l.Movement).Include(l => l.Product).ThenInclude(p => p.BaseUnit)
                    .Include(l => l.BalanceChanges).SingleOrDefaultAsync(token);
            }
            else
            {
                var entry = await StagingEntries(location.Id).Include(x => x.Lines).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
                    .Include(x => x.Lines).ThenInclude(x => x.BalanceChanges).SingleOrDefaultAsync(x => x.Id == command.MovementId, token);
                line = entry?.Lines.Single();
            }
            if (line is null) return Invalid("La entrada no está vigente o no pertenece a STAGING.");
            var movement = line.Movement;
            if (!line.Product.IsActive || command.Quantity > line.Quantity ||
                (!line.Product.BaseUnit.AllowsDecimals && decimal.Truncate(command.Quantity) != command.Quantity))
                return Invalid("La cantidad debe respetar la unidad y no superar lo recibido en esta entrada.");
            var owned = command.ArrivalLineId.HasValue
                ? await db.PalletPlates.Where(p => db.PalletPlateEvents.Any(e => e.PlateId == p.Id && e.MovementLineId == line.Id)).ToListAsync(token)
                : await db.PalletPlates.Where(x => x.OriginMovementId == movement.Id).ToListAsync(token);
            var ownedIds = owned.Select(x => x.Id).ToArray();
            var ownedEvents = await db.PalletPlateEvents.Where(x => ownedIds.Contains(x.PlateId)).ToListAsync(token);
            if (ownedEvents.Any(x => x.Kind == "StagingEntryIdentification" && (!command.ArrivalLineId.HasValue || x.MovementLineId == line.Id)) ||
                (!command.ArrivalLineId.HasValue && owned.Count > 0 && !HasMixedHistory(ownedEvents)))
                return Invalid("Esta entrada ya tiene placas. Consulta su cantidad y ubicación actuales.");

            var state = await IdentificationStateAsync(line.ProductId, location.Id, token);
            if (await db.ProductionWarehouseReservations.AnyAsync(x => x.LocationId == location.Id &&
                x.SupplyRequestLine.ProductId == line.ProductId && x.Quantity > x.ReleasedQuantity, token) ||
                await db.ProductionSupplyPreparationSources.AnyAsync(x => x.LocationId == location.Id &&
                    x.Preparation.SupplyRequestLine.ProductId == line.ProductId && x.Preparation.Status == ProductionSupplyPreparationStatus.Open, token))
                return Invalid("El producto tiene reservas pendientes. Concilia las reservas antes de separar entradas históricas.");
            // Historical recovery is a physical confirmation, restricted to the entry's documented lots.
            var lotIds = line.BalanceChanges.Where(x => x.LocationId == location.Id && x.DeltaQuantity > 0 && x.LotId.HasValue)
                .Select(x => x.LotId!.Value).ToHashSet();
            if (line.LotId.HasValue) lotIds.Add(line.LotId.Value);
            // Pre-lot entries can be recovered by the same physical confirmation. Current lot
            // allocation is recorded in the new event; the historical movement is never rewritten.
            if (lotIds.Count == 0) lotIds.UnionWith(state.Lots.Select(x => x.Id));
            if (lotIds.Count == 0) return Invalid("La entrada no conserva lotes suficientes para identificarla. Revisa su historial antes de imprimir.");
            var sources = await db.PalletPlates.Include(x => x.Lots)
                .Where(x => x.ProductId == line.ProductId && x.LocationId == location.Id && !x.IsVoided && x.Quantity > 0)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token);
            var sourceIds = sources.Select(x => x.Id).ToArray();
            var sourceEvents = await db.PalletPlateEvents.Where(x => sourceIds.Contains(x.PlateId)).ToListAsync(token);
            var eligible = new List<PalletPlate>();
            foreach (var source in sources)
            {
                var history = sourceEvents.Where(x => x.PlateId == source.Id).ToArray();
                if (knownArrivalPlates.Any(p => p.PlateId == source.Id) && !await HasPlateAssignmentsAsync(source.Id, token))
                { eligible.Add(source); continue; }
                if (history.Any(x => x.Kind == "StagingEntryIdentification") ||
                    (source.OriginMovementId.HasValue && !HasMixedHistory(history)) || await HasPlateAssignmentsAsync(source.Id, token)) continue;
                eligible.Add(source);
            }
            var available = lotIds.ToDictionary(id => id, id => Math.Max(0, state.FreeLots.GetValueOrDefault(id) +
                eligible.SelectMany(x => x.Lots).Where(x => x.LotId == id).Sum(x => x.Quantity)));
            var eligibleIds = eligible.Select(x => x.Id).ToArray();
            var otherLots = await db.PalletPlateLots.AsNoTracking().Where(x => x.Plate.ProductId == line.ProductId &&
                x.Plate.LocationId == location.Id && !x.Plate.IsVoided && !eligibleIds.Contains(x.PlateId) && x.Quantity > 0).ToListAsync(token);
            // Do not use plate subdivisions to conceal a negative or already committed balance.
            foreach (var id in lotIds) available[id] = Math.Min(available[id], Math.Max(0,
                state.Balances.Where(x => x.LotId == id).Sum(x => x.Quantity) - otherLots.Where(x => x.LotId == id).Sum(x => x.Quantity)));
            if (available.Values.Sum() < command.Quantity || state.Summary.Total < command.Quantity)
                return Invalid("El saldo disponible de los lotes de esta entrada no cubre la cantidad. Revisa las placas, reservas y salidas.");
            var knownLots = eligible.Where(p => knownArrivalPlates.Any(k => k.PlateId == p.Id)).SelectMany(p => p.Lots)
                .GroupBy(l => l.LotId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
            var allocations = PalletPlateEngine.Allocate(command.Quantity - knownLots.Values.Sum(), state.Lots.Where(x => lotIds.Contains(x.Id)),
                    id => Math.Max(0, available.GetValueOrDefault(id) - knownLots.GetValueOrDefault(id)), lotIds.First())
                .Concat(knownLots.Select(l => new InventoryLotSelection(l.Key, l.Value)))
                .GroupBy(l => l.LotId).Select(g => new InventoryLotSelection(g.Key, g.Sum(l => l.Quantity))).ToArray();
            var plate = new PalletPlate { ProductId = line.ProductId, LocationId = location.Id,
                OriginMovementId = movement.Id, CreatedAt = timeProvider.GetUtcNow(), IsVoided = true };
            var before = PalletPlateEngine.State(plate);
            var engine = new PalletPlateEngine(db);
            var changed = new Dictionary<Guid, PalletState>();
            foreach (var allocation in allocations)
            {
                var pending = Math.Max(knownLots.GetValueOrDefault(allocation.LotId), allocation.Quantity - state.FreeLots.GetValueOrDefault(allocation.LotId));
                foreach (var source in eligible.OrderByDescending(p => knownArrivalPlates.Any(k => k.PlateId == p.Id)))
                {
                    var take = Math.Min(pending, Math.Max(0, source.Lots.SingleOrDefault(x => x.LotId == allocation.LotId)?.Quantity ?? 0));
                    if (take == 0) continue;
                    changed.TryAdd(source.Id, PalletPlateEngine.State(source));
                    PalletPlateEngine.Change(source, allocation.LotId, -take);
                    pending -= take;
                }
                PalletPlateEngine.Change(plate, allocation.LotId, allocation.Quantity);
            }
            foreach (var source in eligible.Where(x => changed.ContainsKey(x.Id)))
                engine.Record(source, changed[source.Id], command.OperationId, null, plate.CreatedAt,
                    "StagingEntrySeparation", movement, line, fingerprint: fingerprint);
            plate.IsVoided = false;
            db.PalletPlates.Add(plate);
            engine.Record(plate, before, command.OperationId, null, plate.CreatedAt,
                "StagingEntryIdentification", movement, line, fingerprint: fingerprint);
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(InventoryMovementStatus.Success, Plates: [PalletPlateEngine.Result(plate)]);
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return Invalid("El saldo o las placas cambiaron. Consulta nuevamente la entrada.");
        }
    }

    public async Task<bool> CanPrintStagingPlatesAsync(Guid locationId, IReadOnlyCollection<Guid> ids, CancellationToken token = default)
    {
        if (ids.Count is 0 or > 100 || !await IsStagingAsync(locationId, token)) return false;
        var effective = StagingEntries(locationId).Select(x => x.Id);
        var selected = await db.PalletPlates.AsNoTracking().Where(x => ids.Contains(x.Id) && x.LocationId == locationId &&
            !x.IsVoided && x.Quantity > 0 && x.OriginMovementId.HasValue && effective.Contains(x.OriginMovementId.Value)).ToListAsync(token);
        if (selected.Count != ids.Distinct().Count()) return false;
        var events = await db.PalletPlateEvents.AsNoTracking().Where(x => ids.Contains(x.PlateId)).ToListAsync(token);
        if (HasMixedHistory(events)) return false;
        return await StagingLotsCoveredAsync(locationId, selected.Select(x => x.ProductId).Distinct().ToArray(), token);
    }

    public async Task<bool> CanPrintStagingArrivalAsync(Guid arrivalLineId, Guid locationId,
        IReadOnlyCollection<Guid> ids, string version, CancellationToken token = default)
    {
        if (ids.Count is 0 or > 100) return false;
        var row = await new StagingArrivalQuery(db).GetAsync(arrivalLineId, token);
        if (row is null || row.NeedsIdentification || row.Pending <= 0 || row.LocationId != locationId || row.Version != version)
            return false;
        var selection = StagingArrivalQuery.DecodeVersion(row.Version);
        if (selection is null || !selection.Select(p => p.PlateId).ToHashSet().IsSupersetOf(ids)) return false;
        return await StagingLotsCoveredAsync(locationId, [row.ProductId], token);
    }

    private async Task<bool> StagingLotsCoveredAsync(Guid locationId, Guid[] products, CancellationToken token)
    {
        var balances = await db.InventoryBalances.AsNoTracking().Where(x => x.LocationId == locationId && products.Contains(x.ProductId))
            .ToListAsync(token);
        var lots = await db.PalletPlateLots.AsNoTracking().Where(x => x.Plate.LocationId == locationId &&
            products.Contains(x.Plate.ProductId) && !x.Plate.IsVoided).ToListAsync(token);
        return lots.GroupBy(x => x.LotId).All(g => g.Sum(x => x.Quantity) <= balances.Where(x => x.LotId == g.Key).Sum(x => x.Quantity));
    }
}
