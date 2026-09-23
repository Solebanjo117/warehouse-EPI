using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

internal sealed class PalletPlateException(string message) : Exception(message);

internal sealed record PalletState(Guid LocationId, decimal Quantity, bool IsVoided, Dictionary<Guid, decimal> Lots, string? Reason = null);

internal sealed class PalletPlateEngine(WarehouseDbContext db)
{
    internal static PalletState State(PalletPlate p) => new(p.LocationId, p.Quantity, p.IsVoided,
        p.Lots.ToDictionary(x => x.LotId, x => x.Quantity));

    internal static PalletMovementResult Result(PalletPlate p) => new(p.Id, p.Identifier, p.LocationId, p.Quantity, p.Version, p.Status);

    internal void Record(PalletPlate p, PalletState before, Guid operationId, Guid? userId, DateTimeOffset now,
        string kind, InventoryMovement? movement = null, InventoryMovementLine? line = null,
        Guid? reverses = null, string fingerprint = "")
    {
        p.Version++;
        db.PalletPlateEvents.Add(new()
        {
            Plate = p, PlateId = p.Id, PlateVersion = p.Version, OperationId = operationId,
            MovementId = movement?.Id, MovementLineId = line?.Id, ResponsibleUserId = userId,
            Kind = kind, Before = JsonSerializer.Serialize(before), After = JsonSerializer.Serialize(State(p)),
            RecordedAt = now, ReversesEventId = reverses, Fingerprint = fingerprint
        });
    }

    internal static void Change(PalletPlate p, Guid lotId, decimal delta)
    {
        var lot = p.Lots.SingleOrDefault(x => x.LotId == lotId);
        if (lot is null) { lot = new() { Plate = p, PlateId = p.Id, LotId = lotId }; p.Lots.Add(lot); }
        lot.Quantity += delta;
        p.Quantity = p.Lots.Sum(x => x.Quantity);
        if (Math.Abs(p.Quantity) > InventoryMovementRules.MaximumQuantity || Math.Abs(lot.Quantity) > InventoryMovementRules.MaximumQuantity)
            throw new PalletPlateException("La cantidad de la placa excede la precisión permitida.");
    }

    internal static List<InventoryLotSelection> Allocate(decimal quantity, IEnumerable<ProductLot> lots,
        Func<Guid, decimal> available, Guid fallback)
    {
        var result = new List<InventoryLotSelection>();
        foreach (var lot in lots.OrderBy(x => x.LotDate is null).ThenBy(x => x.LotDate).ThenBy(x => x.CreatedAt).ThenBy(x => x.NormalizedNumber))
        {
            var take = Math.Min(quantity, Math.Max(0, available(lot.Id)));
            if (take > 0) { result.Add(new(lot.Id, take)); quantity -= take; fallback = lot.Id; }
            if (quantity == 0) break;
        }
        if (quantity > 0)
        {
            var i = result.FindIndex(x => x.LotId == fallback);
            if (i < 0) result.Add(new(fallback, quantity)); else result[i] = result[i] with { Quantity = result[i].Quantity + quantity };
        }
        return result;
    }

    internal async Task ApplyAsync(InventoryMovement movement, InventoryMovementLine line,
        InventoryMovementLineCommand command, IReadOnlyDictionary<InventoryBalanceKey, InventoryBalance> balances,
        IReadOnlyList<ProductLot> lots, ProductLot daily, bool allowsDecimals, bool allowReservedWip, Guid? ownSupplyLineId, CancellationToken token)
    {
        var locationIds = InventoryMovementRules.GetLocations(command, movement.Type).ToArray();
        var plates = await db.PalletPlates.Include(x => x.Lots)
            .Where(x => x.ProductId == command.ProductId && locationIds.Contains(x.LocationId) && !x.IsVoided).ToListAsync(token);
        // Include plates created earlier in a multi-line transaction.
        plates = plates.Concat(db.PalletPlates.Local.Where(x => x.ProductId == command.ProductId && locationIds.Contains(x.LocationId) && !x.IsVoided))
            .DistinctBy(x => x.Id).ToList();
        void CheckQuantity(decimal q, bool positive = true)
        {
            if ((positive ? q <= 0 : q < 0) || Math.Abs(q) > InventoryMovementRules.MaximumQuantity || decimal.Round(q, 4) != q || (!allowsDecimals && decimal.Truncate(q) != q))
                throw new PalletPlateException("Las cantidades por pallet no son válidas para la unidad del producto.");
        }
        PalletPlate Find(Guid id, Guid location, long? version)
        {
            var p = plates.SingleOrDefault(x => x.Id == id && x.LocationId == location);
            if (p is null) throw new PalletPlateException("La placa no pertenece al producto y ubicación seleccionados o fue anulada.");
            var changedInThisMovement = db.PalletPlateEvents.Local.Any(e => e.PlateId == p.Id && e.MovementId == movement.Id);
            if (version != p.Version && !(changedInThisMovement && version == db.Entry(p).OriginalValues.GetValue<long>(nameof(PalletPlate.Version))))
                throw new PalletPlateException("La placa cambió. Consulta nuevamente su cantidad y ubicación.");
            return p;
        }
        var touched = new Dictionary<Guid, (PalletPlate Plate, PalletState Before)>();
        void Touch(PalletPlate p) => touched.TryAdd(p.Id, (p, State(p)));
        PalletPlate NewPlate(Guid location, Guid? id = null)
        {
            var p = new PalletPlate { Id = id ?? Guid.NewGuid(), ProductId = command.ProductId, LocationId = location,
                OriginMovementId = movement.Id, CreatedAt = movement.RecordedAt, IsVoided = true };
            Touch(p); p.IsVoided = false; db.PalletPlates.Add(p); plates.Add(p); return p;
        }
        if (movement.Type == InventoryMovementType.Entry)
        {
            // Older integrations can still receive unplated stock. All updated entry forms
            // explicitly send their distribution (one pallet by default).
            if (command.PalletQuantities is null && !command.AutomaticPalletHandling)
            {
                InventoryLotEngine.ApplyTrackedLine(movement.Type, command, line, balances, lots, daily, movement.RecordedAt);
                return;
            }
            var quantities = command.PalletQuantities ?? [command.Quantity];
            if (quantities.Count == 0 || quantities.Count > 1000 || quantities.Sum() != command.Quantity)
                throw new PalletPlateException("La suma de los pallets debe coincidir con la cantidad recibida (máximo 1000 pallets).");
            foreach (var q in quantities) CheckQuantity(q);
            InventoryLotEngine.ApplyTrackedLine(movement.Type, command, line, balances, lots, daily, movement.RecordedAt);
            foreach (var q in quantities)
            {
                var p = NewPlate(command.DestinationLocationId!.Value,
                    line.LineNumber == 1 && touched.Count == 0 ? movement.Id : null);
                Change(p, command.DestinationLotId ?? daily.Id, q);
            }
        }
        else if (movement.Type == InventoryMovementType.Adjustment)
        {
            var existing = plates.Where(x => x.LocationId == command.LocationId && x.Quantity != 0)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray();
            if (!command.AutomaticPalletHandling &&
                ((command.PlateCounts ?? []).Select(x => x.PlateId).Distinct().Count() != (command.PlateCounts ?? []).Count ||
                 existing.Any(x => (command.PlateCounts ?? []).All(c => c.PlateId != x.Id))))
                throw new PalletPlateException("Captura la cantidad real de cada placa de la ubicación antes de conciliar el conteo.");
            var reservedCounts = await Production.ProductionPlateAllocation.ReservedAsync(db, command.ProductId, command.LocationId!.Value, token);
            var preparedCountsJson = await db.ProductionSupplyPreparationSources.Where(x => x.LocationId == command.LocationId
                && x.Preparation.Status == ProductionSupplyPreparationStatus.Open && x.Preparation.SupplyRequestLine.ProductId == command.ProductId)
                .Select(x => x.PlatesJson).ToListAsync(token);
            var preparedCounts = preparedCountsJson.SelectMany(x => JsonSerializer.Deserialize<List<PalletSelection>>(x) ?? [])
                .GroupBy(x => x.PlateId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
            if (command.AutomaticPalletHandling && existing.Length > 1)
            {
                var secondaryIds = existing.Skip(1).Select(x => x.Id).ToArray();
                if (secondaryIds.Any(id => preparedCounts.GetValueOrDefault(id) > 0 || reservedCounts.Keys.Any(key => key.PlateId == id)))
                    throw new PalletPlateException("Hay placas duplicadas con material reservado o preparado. Concilia esas reservas antes de ajustar el pallet.");
                var canonical = existing[0];
                Touch(canonical);
                foreach (var secondary in existing.Skip(1))
                {
                    Touch(secondary);
                    foreach (var lot in secondary.Lots.Where(x => x.Quantity != 0).ToArray())
                    {
                        Change(canonical, lot.LotId, lot.Quantity);
                        Change(secondary, lot.LotId, -lot.Quantity);
                    }
                }
            }
            IReadOnlyList<PalletSelection> counts = command.AutomaticPalletHandling && existing.Length > 0
                ? [new(existing[0].Id, command.Quantity, existing[0].Version)]
                : command.PlateCounts ?? [];
            var deltas = new Dictionary<Guid, decimal>();
            foreach (var c in counts)
            {
                CheckQuantity(c.Quantity, false);
                var p = Find(c.PlateId, command.LocationId!.Value, c.ExpectedVersion); Touch(p);
                if (c.Quantity < Math.Max(preparedCounts.GetValueOrDefault(p.Id), reservedCounts.Where(x => x.Key.PlateId == p.Id).Sum(x => x.Value)))
                    throw new PalletPlateException("La cantidad contada de la placa es menor que el material reservado o preparado. Concilia primero las reservas de producción.");
                var delta = c.Quantity - p.Quantity;
                var allocations = delta >= 0 ? new List<InventoryLotSelection> { new(daily.Id, delta) }
                    : Allocate(-delta, lots, id => p.Lots.SingleOrDefault(x => x.LotId == id)?.Quantity ?? 0, daily.Id);
                foreach (var a in allocations)
                {
                    if (delta < 0 && a.Quantity > Math.Max(0, (p.Lots.SingleOrDefault(x => x.LotId == a.LotId)?.Quantity ?? 0) - reservedCounts.GetValueOrDefault((p.Id, a.LotId))))
                        throw new PalletPlateException("El ajuste afecta lotes reservados de la placa. Concilia primero las reservas de producción.");
                    var signed = delta >= 0 ? a.Quantity : -a.Quantity;
                    Change(p, a.LotId, signed); deltas[a.LotId] = deltas.GetValueOrDefault(a.LotId) + signed;
                }
            }
            var current = balances.Where(x => x.Key.ProductId == command.ProductId && x.Key.LocationId == command.LocationId).Sum(x => x.Value.Quantity);
            var remainder = command.Quantity - current - deltas.Values.Sum();
            var remainderLots = remainder >= 0 ? new List<InventoryLotSelection> { new(daily.Id, remainder) }
                : Allocate(-remainder, lots, id => balances[new(command.ProductId, command.LocationId!.Value, id)].Quantity + deltas.GetValueOrDefault(id)
                    - plates.Where(p => p.LocationId == command.LocationId).SelectMany(p => p.Lots).Where(x => x.LotId == id).Sum(x => x.Quantity), daily.Id);
            foreach (var a in remainderLots) deltas[a.LotId] = deltas.GetValueOrDefault(a.LotId) + (remainder >= 0 ? a.Quantity : -a.Quantity);
            line.PreviousQuantity = current; line.AdjustmentDelta = command.Quantity - current;
            foreach (var d in deltas.Where(x => x.Value != 0))
                InventoryLotEngine.ApplyChange(line, balances[new(command.ProductId, command.LocationId!.Value, d.Key)], d.Value, movement.RecordedAt, lots.Single(x => x.Id == d.Key));
        }
        else
        {
            IReadOnlyList<PalletSelection> selections = command.Plates ?? [];
            var allocations = new List<InventoryLotSelection>();
            List<Production.ProductionPlateLot>? owned = null;
            List<InventoryLotSelection>? ownedUnplated = null;
            if (command.MaterialIssueLinkId is Guid issueId)
            {
                if (!allowReservedWip) throw new PalletPlateException("La reserva debe operarse desde la orden de producción.");
                var issue = await db.ProductionMaterialIssueLinks.Include(x => x.Lots).Include(x => x.OperationLines).ThenInclude(x => x.Operation)
                    .Include(x => x.OperationLines).ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
                    .SingleAsync(x => x.Id == issueId, token);
                owned = await Production.ProductionPlateAllocation.RemainingAsync(db, issue, token);
                var reversed = await db.ProductionMaterialOperations.Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
                ownedUnplated = InventoryMovementService.RemainingLots(issue, reversed).Select(x => new InventoryLotSelection(x.LotId,
                    x.Quantity - owned.Where(p => p.LotId == x.LotId).Sum(p => p.Quantity))).ToList();
            }
            var reserved = allowReservedWip ? new Dictionary<(Guid PlateId, Guid LotId), decimal>()
                : await Production.ProductionPlateAllocation.ReservedAsync(db, command.ProductId, command.SourceLocationId!.Value, token);
            var reservedUnplated = new Dictionary<Guid, decimal>();
            if (command.AutomaticPalletHandling && !allowReservedWip)
            {
                var links = await db.ProductionMaterialIssueLinks.Include(x => x.Lots).Include(x => x.OperationLines).ThenInclude(x => x.Operation)
                    .Include(x => x.OperationLines).ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
                    .Where(x => x.ProductId == command.ProductId && x.WipLocationId == command.SourceLocationId).ToListAsync(token);
                var reversed = await db.ProductionMaterialOperations.Where(x => x.ReversesOperationId != null)
                    .Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
                var totalReserved = links.SelectMany(x => InventoryMovementService.RemainingLots(x, reversed))
                    .GroupBy(x => x.LotId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
                reservedUnplated = totalReserved.ToDictionary(x => x.Key,
                    x => Math.Max(0, x.Value - reserved.Where(r => r.Key.LotId == x.Key).Sum(r => r.Value)));
            }
            var parts = new List<(PalletPlate Plate, decimal Quantity, List<InventoryLotSelection> Lots)>();
            var preparations = await db.ProductionSupplyPreparationSources.Where(x => x.LocationId == command.SourceLocationId
                && x.Preparation.Status == ProductionSupplyPreparationStatus.Open && x.Preparation.SupplyRequestLine.ProductId == command.ProductId
                && x.Preparation.SupplyRequestLineId != ownSupplyLineId).Select(x => x.PlatesJson).ToListAsync(token);
            var prepared = preparations.SelectMany(x => JsonSerializer.Deserialize<List<PalletSelection>>(x) ?? []).GroupBy(x => x.PlateId)
                .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
            if (command.AutomaticPalletHandling && selections.Count == 0)
            {
                var remaining = command.Quantity;
                var automatic = new List<PalletSelection>();
                foreach (var p in plates.Where(x => x.LocationId == command.SourceLocationId && x.Quantity > 0)
                             .OrderBy(x => x.CreatedAt).ThenBy(x => x.Identifier).ThenBy(x => x.Id))
                {
                    var availableByLots = owned is null
                        ? p.Lots.Sum(x => Math.Max(0, x.Quantity - reserved.GetValueOrDefault((p.Id, x.LotId))))
                        : owned.Where(x => x.PlateId == p.Id).Sum(x => x.Quantity);
                    var available = Math.Min(availableByLots, Math.Max(0, p.Quantity - prepared.GetValueOrDefault(p.Id)));
                    var take = Math.Min(remaining, available);
                    if (take <= 0) continue;
                    automatic.Add(new(p.Id, take, p.Version));
                    remaining -= take;
                    if (remaining == 0) break;
                }
                selections = automatic;
            }
            if (selections.Select(x => x.PlateId).Distinct().Count() != selections.Count || selections.Sum(x => x.Quantity) > command.Quantity)
                throw new PalletPlateException("Revisa las placas seleccionadas: no deben repetirse ni superar la cantidad del movimiento.");
            foreach (var s in selections)
            {
                CheckQuantity(s.Quantity);
                var p = Find(s.PlateId, command.SourceLocationId!.Value, s.ExpectedVersion);
                if (prepared.GetValueOrDefault(p.Id) > 0 && s.Quantity > Math.Max(0, p.Quantity - prepared[p.Id]))
                    throw new PalletPlateException("La placa está preparada para otra solicitud de producción. Revisa esa preparación antes de moverla.");
                if (owned is not null && s.Quantity > owned.Where(x => x.PlateId == p.Id).Sum(x => x.Quantity))
                    throw new PalletPlateException("La cantidad supera el material de esta placa reservado para la orden.");
                var take = Allocate(s.Quantity, lots, id => owned is null
                    ? (p.Lots.SingleOrDefault(x => x.LotId == id)?.Quantity ?? 0) - (command.AutomaticPalletHandling ? reserved.GetValueOrDefault((p.Id, id)) : 0)
                    : owned.Where(x => x.PlateId == p.Id && x.LotId == id).Sum(x => x.Quantity), daily.Id);
                if (owned is null && movement.Type == InventoryMovementType.Transfer && command.DestinationPlateId is null && s.Quantity == p.Quantity && p.Quantity > 0)
                    take = p.Lots.Where(x => x.Quantity != 0).Select(x => new InventoryLotSelection(x.LotId, x.Quantity)).ToList();
                if (take.Any(a => reserved.GetValueOrDefault((p.Id, a.LotId)) > 0 &&
                    a.Quantity > Math.Max(0, (p.Lots.SingleOrDefault(x => x.LotId == a.LotId)?.Quantity ?? 0) - reserved.GetValueOrDefault((p.Id, a.LotId)))))
                    throw new PalletPlateException("La placa contiene material reservado para una orden. Opera desde la orden correspondiente.");
                allocations.AddRange(take); parts.Add((p, s.Quantity, take));
            }
            var unplated = command.Quantity - selections.Sum(x => x.Quantity);
            if (ownedUnplated is not null && unplated > ownedUnplated.Sum(x => x.Quantity))
                throw new PalletPlateException("Selecciona las placas del material reservado. El saldo reservado sin placa no cubre la cantidad.");
            if (unplated > 0)
                allocations.AddRange(Allocate(unplated, lots, id => ownedUnplated is not null ? ownedUnplated.Where(x => x.LotId == id).Sum(x => x.Quantity)
                    : balances[new(command.ProductId, command.SourceLocationId!.Value, id)].Quantity
                    - plates.Where(p => p.LocationId == command.SourceLocationId).SelectMany(p => p.Lots).Where(x => x.LotId == id).Sum(x => x.Quantity)
                    - reservedUnplated.GetValueOrDefault(id), daily.Id));
            var selectedLots = allocations.GroupBy(x => x.LotId).Select(x => new InventoryLotSelection(x.Key, x.Sum(y => y.Quantity))).ToArray();
            if (command.Lots is { Count: > 0 } && !command.Lots.OrderBy(x => x.LotId).SequenceEqual(selectedLots.OrderBy(x => x.LotId)))
            {
                // Legacy reserved lots are authoritative for unplated material; identified material must agree exactly.
                if (selections.Count > 0) throw new PalletPlateException("Los lotes de la placa no coinciden con el material reservado para esta operación.");
                selectedLots = command.Lots.ToArray();
                foreach (var a in selectedLots)
                    if (plates.Any(p => p.LocationId == command.SourceLocationId && p.Lots.Any(l => l.LotId == a.LotId && l.Quantity != 0)))
                    {
                        var residual = balances[new(command.ProductId, command.SourceLocationId!.Value, a.LotId)].Quantity
                            - plates.Where(p => p.LocationId == command.SourceLocationId).SelectMany(p => p.Lots).Where(l => l.LotId == a.LotId).Sum(l => l.Quantity);
                        if (a.Quantity > residual) throw new PalletPlateException("Identifica la placa del material reservado antes de consumirlo.");
                    }
            }
            InventoryLotEngine.ApplyTrackedLine(movement.Type, command with { Lots = selectedLots }, line, balances, lots, daily, movement.RecordedAt);
            PalletPlate? target = null;
            if (movement.Type == InventoryMovementType.Transfer && command.DestinationPlateId is Guid targetId)
            { target = Find(targetId, command.DestinationLocationId!.Value, command.ExpectedDestinationPlateVersion); Touch(target); }
            else if (movement.Type == InventoryMovementType.Transfer && command.AutomaticPalletHandling && parts.Count > 0)
            {
                target = plates.Where(x => x.LocationId == command.DestinationLocationId && x.Quantity > 0)
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Identifier).ThenBy(x => x.Id).FirstOrDefault();
                if (target is not null) Touch(target);
            }
            foreach (var part in parts)
            {
                Touch(part.Plate);
                if (movement.Type == InventoryMovementType.Transfer && target is null && part.Quantity == part.Plate.Quantity && part.Plate.Quantity > 0 &&
                    part.Lots.OrderBy(x => x.LotId).SequenceEqual(part.Plate.Lots.Where(x => x.Quantity != 0).Select(x => new InventoryLotSelection(x.LotId, x.Quantity)).OrderBy(x => x.LotId)))
                    part.Plate.LocationId = command.DestinationLocationId!.Value;
                else
                {
                    var destination = movement.Type == InventoryMovementType.Transfer ? target ?? NewPlate(command.DestinationLocationId!.Value) : null;
                    if (destination is not null && target is null) destination.OriginMovementId = part.Plate.OriginMovementId;
                    foreach (var a in part.Lots)
                    { Change(part.Plate, a.LotId, -a.Quantity); if (destination is not null) Change(destination, a.LotId, a.Quantity); }
                }
            }
            // The unplated part of a transfer stays unplated at destination. Only quantities
            // that already belonged to a plate may preserve, split or merge their identity.
        }
        foreach (var item in touched.Values)
            Record(item.Plate, item.Before, movement.OperationId, movement.ResponsibleUserId, movement.RecordedAt,
                movement.Type.ToString(), movement, line);
    }

    internal async Task<string?> ReversalErrorAsync(Guid movementId, CancellationToken token)
    {
        var originals = await db.PalletPlateEvents.Where(x => x.MovementId == movementId && x.ReversesEventId == null).ToListAsync(token);
        foreach (var original in originals)
        {
            var later = await db.PalletPlateEvents.Where(x => x.PlateId == original.PlateId && x.PlateVersion > original.PlateVersion
                && x.MovementId != movementId && x.ReversesEventId == null
                && !db.PalletPlateEvents.Any(r => r.ReversesEventId == x.Id)).OrderBy(x => x.PlateVersion).FirstOrDefaultAsync(token);
            if (later is not null) return $"Revierte primero la operación {later.OperationId} de la placa PLT-{original.PlateId:N}.";
        }
        return null;
    }

    internal async Task ReverseAsync(InventoryMovement original, InventoryMovement reversal, CancellationToken token)
    {
        var error = await ReversalErrorAsync(original.Id, token);
        if (error is not null) throw new PalletPlateException(error);
        var events = await db.PalletPlateEvents.Include(x => x.Plate).ThenInclude(x => x.Lots)
            .Where(x => x.MovementId == original.Id && x.ReversesEventId == null).OrderByDescending(x => x.PlateVersion).ToListAsync(token);
        foreach (var e in events)
        {
            var p = e.Plate; var before = State(p);
            var state = JsonSerializer.Deserialize<PalletState>(e.Before)!;
            p.LocationId = state.LocationId; p.IsVoided = state.IsVoided;
            foreach (var lot in p.Lots) lot.Quantity = state.Lots.GetValueOrDefault(lot.LotId);
            foreach (var lot in state.Lots.Where(x => p.Lots.All(l => l.LotId != x.Key))) p.Lots.Add(new() { Plate = p, PlateId = p.Id, LotId = lot.Key, Quantity = lot.Value });
            p.Quantity = state.Quantity;
            Record(p, before, reversal.OperationId, reversal.ResponsibleUserId, reversal.RecordedAt, "Reversal", reversal, reverses: e.Id);
        }
    }
}
