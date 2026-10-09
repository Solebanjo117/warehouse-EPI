using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record StagingPlateSplitCommand(Guid OperationId, Guid ArrivalLineId, Guid PlateId,
    long ExpectedVersion, string ArrivalVersion, IReadOnlyList<decimal> Quantities, string Pin);

public static class PalletDistribution
{
    public static bool TryCalculateTotal(IReadOnlyList<decimal> quantities, bool allowsDecimals, out decimal total)
    {
        total = 0;
        // Bound each value before summing, including malicious decimal inputs.
        if (quantities.Count is < 2 or > 100 || quantities.Any(q => q < 0 || q > InventoryMovementRules.MaximumQuantity)) return false;
        total = quantities.Sum();
        return total <= InventoryMovementRules.MaximumQuantity && IsValid(quantities, total, allowsDecimals);
    }

    public static bool IsValid(IReadOnlyList<decimal> quantities, decimal total, bool allowsDecimals) =>
        quantities.Count is >= 2 and <= 100 && quantities.All(q => q > 0 && q <= total &&
            q <= 99_999_999_999_999.9999m && decimal.Round(q, 4) == q && (allowsDecimals || decimal.Truncate(q) == q)) &&
        quantities.Sum() == total;
}

public sealed partial class PalletTrackingService
{
    public async Task<InventoryMovementResult> SplitStagingPlateAsync(StagingPlateSplitCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user is null) return new(InventoryMovementStatus.InvalidPin);
        if (!RoleAccess.CanOperateWarehouse(user.Role.Code)) return new(InventoryMovementStatus.RoleNotAllowed, Errors: [RoleAccess.WarehouseWarning]);
        if (command.OperationId == Guid.Empty || command.Quantities is null || command.Quantities.Count is < 2 or > 100)
            return Invalid("Distribuye la cantidad completa entre 2 y 100 partes válidas.");
        var fingerprint = Fingerprint(command with { Pin = user.Id.ToString() });
        async Task<InventoryMovementResult?> RetryAsync()
        {
            var prior = await db.PalletPlateEvents.AsNoTracking().SingleOrDefaultAsync(e => e.OperationId == command.OperationId && e.Kind == "StagingSplitSource", token);
            if (prior is null) return null;
            if (prior.Fingerprint != fingerprint) return new(InventoryMovementStatus.IdempotencyConflict);
            var children = await db.PalletPlateEvents.AsNoTracking().Where(e => e.OperationId == command.OperationId && e.Kind == "StagingSplitChild")
                .OrderBy(e => e.PlateId).ToListAsync(token);
            return new(InventoryMovementStatus.Success, Plates: children.Select(e => {
                var state = JsonSerializer.Deserialize<PalletState>(e.After)!;
                return new PalletMovementResult(e.PlateId, $"PLT-{e.PlateId:N}".ToUpperInvariant(), state.LocationId, state.Quantity, e.PlateVersion, "Disponible");
            }).ToArray());
        }
        var retry = await RetryAsync();
        if (retry is not null) return retry;
        var line = await db.InventoryMovementLines.AsNoTracking().SingleOrDefaultAsync(l => l.Id == command.ArrivalLineId, token);
        if (line?.DestinationLocationId is not Guid locationId) return Invalid("Consulta nuevamente la llegada a STAGING.");
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            if (tx is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({command.OperationId.ToString()}, 0))", token);
                await InventoryMovementStore.LockLocationsAsync([locationId], tx, token);
            }
            retry = await RetryAsync();
            if (retry is not null) return retry;
            var arrival = await new StagingArrivalQuery(db).GetAsync(command.ArrivalLineId, token);
            if (arrival is null || arrival.NeedsIdentification || arrival.Pending <= 0 || arrival.Version != command.ArrivalVersion)
                return new(InventoryMovementStatus.BalanceChanged);
            var owned = StagingArrivalQuery.DecodeVersion(arrival.Version)!;
            if (owned.Count - 1 + command.Quantities.Count > 1000)
                return Invalid("La llegada alcanzaría más de 1000 placas pendientes. Acomoda material antes de dividir.");
            if (!owned.Any(p => p.PlateId == command.PlateId && p.ExpectedVersion == command.ExpectedVersion))
                return new(InventoryMovementStatus.BalanceChanged);
            var location = await db.Locations.AsNoTracking().SingleAsync(l => l.Id == locationId, token);
            if (!location.IsOperational || !location.TracksInventory) return Invalid("Selecciona una ubicación activa, físicamente presente y no bloqueada.");
            var product = await db.Products.AsNoTracking().Include(p => p.BaseUnit).SingleAsync(p => p.Id == arrival.ProductId, token);
            if (!product.IsActive || !product.BaseUnit.IsActive) return Invalid("Selecciona un producto activo con existencia positiva en la ubicación.");
            var source = await db.PalletPlates.Include(p => p.Lots).SingleAsync(p => p.Id == command.PlateId, token);
            if (source.Version != command.ExpectedVersion || source.Quantity <= 0 || source.IsVoided || source.LocationId != locationId)
                return new(InventoryMovementStatus.BalanceChanged);
            if (!PalletDistribution.IsValid(command.Quantities, source.Quantity, product.BaseUnit.AllowsDecimals))
                return Invalid("Distribuye la cantidad completa entre 2 y 100 partes válidas.");
            var reservations = await Production.ProductionPlateAllocation.ReservedAsync(db, product.Id, locationId, token);
            var prepared = await db.ProductionSupplyPreparationSources.AsNoTracking().Where(x => x.LocationId == locationId &&
                x.Preparation.Status == ProductionSupplyPreparationStatus.Open && x.Preparation.SupplyRequestLine.ProductId == product.Id)
                .AnyAsync(token);
            var lotIds = source.Lots.Where(l => l.Quantity > 0).Select(l => l.LotId).ToArray();
            if (reservations.Keys.Any(k => k.PlateId == source.Id) || prepared ||
                await db.ProductionWarehouseReservations.AnyAsync(r => r.LocationId == locationId && r.SupplyRequestLine.ProductId == product.Id &&
                    lotIds.Contains(r.LotId) && r.Quantity > r.ReleasedQuantity, token))
                return Invalid("La placa tiene material reservado o preparado. Resuelve esas asignaciones antes de dividir.");
            if (source.Lots.Any(l => l.Quantity < 0) || source.Lots.Sum(l => l.Quantity) != source.Quantity ||
                !await StagingLotsCoveredAsync(locationId, [product.Id], token))
                return Invalid("La composición por lote no cubre la placa. Consulta nuevamente el inventario.");
            var lots = await db.ProductLots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).ToListAsync(token);
            var engine = new PalletPlateEngine(db);
            var now = timeProvider.GetUtcNow();
            var before = PalletPlateEngine.State(source);
            var children = new List<PalletPlate>();
            foreach (var quantity in command.Quantities)
            {
                var child = new PalletPlate { ProductId = product.Id, LocationId = locationId, OriginMovementId = source.OriginMovementId, CreatedAt = now, IsVoided = true };
                var childBefore = PalletPlateEngine.State(child);
                child.IsVoided = false;
                var remaining = quantity;
                foreach (var lot in lots)
                {
                    var take = Math.Min(remaining, source.Lots.Single(l => l.LotId == lot.Id).Quantity);
                    if (take <= 0) continue;
                    PalletPlateEngine.Change(source, lot.Id, -take);
                    PalletPlateEngine.Change(child, lot.Id, take);
                    remaining -= take;
                    if (remaining == 0) break;
                }
                if (remaining != 0) throw new InvalidOperationException("Incomplete split lot allocation.");
                db.PalletPlates.Add(child);
                engine.Record(child, childBefore, command.OperationId, user.Id, now, "StagingSplitChild", line: line, fingerprint: fingerprint);
                children.Add(child);
            }
            // No MovementId: a split is subsequent work and must block an incompatible inventory reversal.
            engine.Record(source, before, command.OperationId, user.Id, now, "StagingSplitSource", line: line, fingerprint: fingerprint);
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(InventoryMovementStatus.Success, Plates: children.OrderBy(p => p.Id).Select(PalletPlateEngine.Result).ToArray());
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return await RetryAsync() ?? new(InventoryMovementStatus.BalanceChanged);
        }
    }
}
