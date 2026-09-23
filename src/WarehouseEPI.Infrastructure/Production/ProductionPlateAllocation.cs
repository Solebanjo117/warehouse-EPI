using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

internal sealed record ProductionPlateLot(Guid PlateId, Guid LotId, decimal Quantity);

internal static class ProductionPlateAllocation
{
    internal static async Task RecordAssignmentAsync(WarehouseDbContext db, ProductionMaterialIssueLink issue, Guid operationId, Guid userId, DateTimeOffset now, CancellationToken token)
    {
        var ids = (JsonSerializer.Deserialize<List<ProductionPlateLot>>(issue.PlateAllocationsJson) ?? []).Select(x => x.PlateId).Distinct().ToArray();
        foreach (var plate in await db.PalletPlates.Include(x => x.Lots).Where(x => ids.Contains(x.Id)).ToListAsync(token))
            new PalletPlateEngine(db).Record(plate, PalletPlateEngine.State(plate), operationId, userId, now, "Assignment", fingerprint: issue.Id.ToString("N"));
    }

    internal static async Task ReleaseAssignmentAsync(WarehouseDbContext db, ProductionMaterialIssueLink issue, Guid operationId, Guid userId, DateTimeOffset now, CancellationToken token)
    {
        if (issue.CancelledQuantity != issue.Quantity) return;
        var key = issue.Id.ToString("N");
        var events = await db.PalletPlateEvents.Include(x => x.Plate).ThenInclude(x => x.Lots)
            .Where(x => x.Kind == "Assignment" && x.Fingerprint == key && !db.PalletPlateEvents.Any(r => r.ReversesEventId == x.Id)).ToListAsync(token);
        foreach (var e in events)
            new PalletPlateEngine(db).Record(e.Plate, PalletPlateEngine.State(e.Plate), operationId, userId, now, "AssignmentCancelled", reverses: e.Id);
    }

    internal static async Task<(List<InventoryLotSelection> Lots, string Plates)> AssignAsync(WarehouseDbContext db,
        Guid productId, Guid locationId, decimal quantity, IReadOnlyList<PalletSelection>? selections, CancellationToken token)
    {
        selections ??= [];
        if (selections.Any(x => x.Quantity <= 0 || decimal.Round(x.Quantity, 4) != x.Quantity) || selections.Sum(x => x.Quantity) > quantity || selections.Select(x => x.PlateId).Distinct().Count() != selections.Count)
            throw new PalletPlateException("Revisa la distribución de placas de la asignación WIP.");
        var plates = await db.PalletPlates.Include(x => x.Lots).Where(x => x.ProductId == productId && x.LocationId == locationId && !x.IsVoided).ToListAsync(token);
        var balances = await db.InventoryBalances.Include(x => x.Lot).Where(x => x.ProductId == productId && x.LocationId == locationId && x.LotId != null).ToListAsync(token);
        var lots = balances.Select(x => x.Lot!).ToArray();
        var reserved = await ReservedAsync(db, productId, locationId, token);
        var chosen = new List<ProductionPlateLot>();
        foreach (var selection in selections)
        {
            var p = plates.SingleOrDefault(x => x.Id == selection.PlateId);
            if (p is null || p.Version != selection.ExpectedVersion) throw new PalletPlateException("La placa WIP cambió. Consulta nuevamente antes de asignar.");
            decimal Free(Guid id) => (p.Lots.SingleOrDefault(x => x.LotId == id)?.Quantity ?? 0) - reserved.GetValueOrDefault((p.Id, id));
            if (selection.Quantity > p.Lots.Sum(x => Math.Max(0, Free(x.LotId)))) throw new PalletPlateException("La placa no tiene suficiente material WIP libre.");
            chosen.AddRange(PalletPlateEngine.Allocate(selection.Quantity, lots, Free, lots[0].Id).Select(x => new ProductionPlateLot(p.Id, x.LotId, x.Quantity)));
        }
        var allocations = chosen.Select(x => new InventoryLotSelection(x.LotId, x.Quantity)).ToList();
        var remainder = quantity - chosen.Sum(x => x.Quantity);
        if (remainder > 0)
        {
            var links = await db.ProductionMaterialIssueLinks.Include(x => x.Lots).Include(x => x.OperationLines).ThenInclude(x => x.Operation)
                .Include(x => x.OperationLines).ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
                .Where(x => x.ProductId == productId && x.WipLocationId == locationId).ToListAsync(token);
            var reversed = await db.ProductionMaterialOperations.Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
            var allReserved = links.SelectMany(x => InventoryMovementService.RemainingLots(x, reversed)).GroupBy(x => x.LotId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
            decimal Free(Guid id) => balances.Single(x => x.LotId == id).Quantity - plates.SelectMany(x => x.Lots).Where(x => x.LotId == id).Sum(x => x.Quantity)
                - allReserved.GetValueOrDefault(id) + reserved.Where(x => x.Key.LotId == id).Sum(x => x.Value);
            if (lots.Length == 0 || lots.Sum(x => Math.Max(0, Free(x.Id))) < remainder) throw new PalletPlateException("El material sin placa no cubre la asignación. Selecciona las placas del material.");
            allocations.AddRange(PalletPlateEngine.Allocate(remainder, lots, Free, lots[0].Id));
        }
        return (allocations.GroupBy(x => x.LotId).Select(x => new InventoryLotSelection(x.Key, x.Sum(y => y.Quantity))).ToList(), JsonSerializer.Serialize(chosen));
    }

    internal static async Task<string> ReceivedAsync(WarehouseDbContext db, Guid lineId, Guid destinationId, CancellationToken token)
    {
        var events = await db.PalletPlateEvents.Where(x => x.MovementLineId == lineId).ToListAsync(token);
        var result = new List<ProductionPlateLot>();
        foreach (var e in events)
        {
            var before = JsonSerializer.Deserialize<PalletState>(e.Before)!;
            var after = JsonSerializer.Deserialize<PalletState>(e.After)!;
            if (after.LocationId != destinationId) continue;
            foreach (var lot in after.Lots)
            {
                var quantity = lot.Value - (before.LocationId == destinationId ? before.Lots.GetValueOrDefault(lot.Key) : 0);
                if (quantity > 0) result.Add(new(e.PlateId, lot.Key, quantity));
            }
        }
        return JsonSerializer.Serialize(result);
    }

    internal static async Task<List<ProductionPlateLot>> RemainingAsync(WarehouseDbContext db, ProductionMaterialIssueLink link, CancellationToken token)
    {
        var result = JsonSerializer.Deserialize<List<ProductionPlateLot>>(link.PlateAllocationsJson) ?? [];
        if (result.Count == 0) return result;
        var lineIds = await db.ProductionMaterialOperationLines.Where(x => x.IssueLinkId == link.Id
            && x.Operation.Type != ProductionMaterialOperationType.Reversal
            && !db.ProductionMaterialOperations.Any(r => r.ReversesOperationId == x.Operation.Id)).Select(x => x.InventoryMovementLineId).ToListAsync(token);
        var events = await db.PalletPlateEvents.Where(x => x.MovementLineId != null && lineIds.Contains(x.MovementLineId.Value) && x.ReversesEventId == null).ToListAsync(token);
        foreach (var e in events)
        {
            var before = JsonSerializer.Deserialize<PalletState>(e.Before)!; var after = JsonSerializer.Deserialize<PalletState>(e.After)!;
            for (var i = 0; i < result.Count; i++)
            {
                var item = result[i]; if (item.PlateId != e.PlateId) continue;
                var consumed = before.Lots.GetValueOrDefault(item.LotId) - (before.LocationId == after.LocationId ? after.Lots.GetValueOrDefault(item.LotId) : 0);
                result[i] = item with { Quantity = Math.Max(0, item.Quantity - Math.Max(0, consumed)) };
            }
        }
        var cancel = link.CancelledQuantity;
        for (var i = 0; i < result.Count && cancel > 0; i++) { var take = Math.Min(cancel, result[i].Quantity); result[i] = result[i] with { Quantity = result[i].Quantity - take }; cancel -= take; }
        return result.Where(x => x.Quantity > 0).ToList();
    }

    internal static async Task<Dictionary<(Guid PlateId, Guid LotId), decimal>> ReservedAsync(WarehouseDbContext db, Guid productId, Guid locationId, CancellationToken token)
    {
        var links = await db.ProductionMaterialIssueLinks.Where(x => x.ProductId == productId && x.WipLocationId == locationId && x.Quantity > x.CancelledQuantity).ToListAsync(token);
        var result = new Dictionary<(Guid, Guid), decimal>();
        foreach (var link in links)
            foreach (var lot in await RemainingAsync(db, link, token)) result[(lot.PlateId, lot.LotId)] = result.GetValueOrDefault((lot.PlateId, lot.LotId)) + lot.Quantity;
        return result;
    }

    internal static async Task<IReadOnlyList<PalletSelection>> SelectAsync(WarehouseDbContext db, ProductionMaterialIssueLink link, decimal quantity, CancellationToken token)
    {
        var remaining = await RemainingAsync(db, link, token);
        var ids = remaining.Select(x => x.PlateId).Distinct().ToArray();
        var plates = await db.PalletPlates.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        var result = new List<PalletSelection>();
        foreach (var group in remaining.GroupBy(x => x.PlateId)
                     .OrderBy(x => plates[x.Key].CreatedAt).ThenBy(x => plates[x.Key].Identifier).ThenBy(x => x.Key))
        {
            var p = plates[group.Key];
            var take = Math.Min(quantity, group.Sum(x => x.Quantity));
            if (take > 0) result.Add(new(p.Id, take, p.Version)); quantity -= take; if (quantity == 0) break;
        }
        return result;
    }

    internal static async Task<IReadOnlyList<PalletSelection>> SelectFreeAsync(WarehouseDbContext db, Guid productId,
        Guid locationId, decimal quantity, Guid? ownSupplyLineId, CancellationToken token)
    {
        var plates = (await db.PalletPlates.Include(x => x.Lots)
            .Where(x => x.ProductId == productId && x.LocationId == locationId && !x.IsVoided && x.Quantity > 0)
            .ToListAsync(token))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Identifier).ThenBy(x => x.Id).ToArray();
        var reserved = await ReservedAsync(db, productId, locationId, token);
        var preparationJson = await db.ProductionSupplyPreparationSources
            .Where(x => x.LocationId == locationId && x.Preparation.Status == ProductionSupplyPreparationStatus.Open &&
                x.Preparation.SupplyRequestLine.ProductId == productId && x.Preparation.SupplyRequestLineId != ownSupplyLineId)
            .Select(x => x.PlatesJson).ToListAsync(token);
        var prepared = preparationJson.SelectMany(x => JsonSerializer.Deserialize<List<PalletSelection>>(x) ?? [])
            .GroupBy(x => x.PlateId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        var result = new List<PalletSelection>();
        foreach (var plate in plates)
        {
            var lotAvailable = plate.Lots.Sum(x => Math.Max(0, x.Quantity - reserved.GetValueOrDefault((plate.Id, x.LotId))));
            var available = Math.Min(lotAvailable, Math.Max(0, plate.Quantity - prepared.GetValueOrDefault(plate.Id)));
            var take = Math.Min(quantity, available);
            if (take <= 0) continue;
            result.Add(new(plate.Id, take, plate.Version));
            quantity -= take;
            if (quantity == 0) break;
        }
        return result;
    }
}
