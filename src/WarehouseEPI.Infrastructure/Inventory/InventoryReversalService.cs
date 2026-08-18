using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Inventory;

internal sealed class InventoryReversalService(
    WarehouseDbContext dbContext,
    TimeProvider timeProvider,
    WarehouseClock? warehouseClock = null)
{
    private readonly WarehouseClock warehouseClock = warehouseClock ?? new(new WarehouseSettingsService(dbContext));
    internal async Task<InventoryMovement> CreateAsync(
        InventoryMovement original,
        Guid authorizedById,
        string correctionFingerprint,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var legacyProducts = original.Lines
            .Where(line => line.BalanceChanges.Any(change => change.LotId is null))
            .Select(line => line.ProductId)
            .Distinct()
            .ToArray();
        var existingLegacyLots = await dbContext.ProductLots
            .Where(lot => legacyProducts.Contains(lot.ProductId))
            .ToListAsync(cancellationToken);
        var legacyLots = existingLegacyLots.GroupBy(lot => lot.ProductId)
            .ToDictionary(group => group.Key, group => group.OrderBy(lot => lot.CreatedAt)
                .ThenBy(lot => lot.NormalizedNumber)
                .First());
        foreach (var productId in legacyProducts.Where(id => !legacyLots.ContainsKey(id)))
        {
            var date = await warehouseClock.GetDateAsync(now, cancellationToken);
            var number = InventoryLotEngine.DailyLotNumber(date);
            var lot = new ProductLot
            {
                ProductId = productId,
                Number = number,
                NormalizedNumber = number,
                LotDate = date,
                CreatedAt = now
            };
            dbContext.ProductLots.Add(lot);
            legacyLots.Add(productId, lot);
        }

        var keys = original.Lines.SelectMany(line => line.BalanceChanges.Select(change =>
                new InventoryBalanceKey(line.ProductId, change.LocationId,
                    change.LotId ?? legacyLots[line.ProductId].Id)))
            .Distinct()
            .ToArray();
        var productIds = keys.Select(key => key.ProductId).Distinct().ToArray();
        var locationIds = keys.Select(key => key.LocationId).Distinct().ToArray();
        var balances = await dbContext.InventoryBalances
            .Where(balance => productIds.Contains(balance.ProductId) && locationIds.Contains(balance.LocationId))
            .ToDictionaryAsync(balance => new InventoryBalanceKey(balance.ProductId, balance.LocationId, balance.LotId), cancellationToken);
        foreach (var key in keys.Where(key => !balances.ContainsKey(key)))
        {
            var balance = new InventoryBalance
            {
                ProductId = key.ProductId,
                LocationId = key.LocationId,
                LotId = key.LotId,
                Quantity = 0m,
                UpdatedAt = timeProvider.GetUtcNow()
            };
            dbContext.InventoryBalances.Add(balance);
            balances.Add(key, balance);
        }

        var reversal = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = InventoryFingerprint.Hash(correctionFingerprint + "|reversal"),
            Type = ReverseType(original.Type),
            ResponsibleUserId = authorizedById,
            Reference = original.Reference,
            Notes = "Reverso de " + original.Id.ToString("N"),
            OccurredAt = now,
            RecordedAt = now
        };
        foreach (var source in original.Lines.OrderBy(line => line.LineNumber))
        {
            var line = new InventoryMovementLine
            {
                LineNumber = source.LineNumber,
                ProductId = source.ProductId,
                UnitId = source.UnitId,
                LotId = source.LotId,
                LotAllocationMode = source.LotAllocationMode
            };
            switch (original.Type)
            {
                case InventoryMovementType.Entry:
                    line.Quantity = source.Quantity;
                    line.SourceLocationId = source.DestinationLocationId;
                    break;
                case InventoryMovementType.Exit:
                    line.Quantity = source.Quantity;
                    line.DestinationLocationId = source.SourceLocationId;
                    break;
                case InventoryMovementType.Transfer:
                    line.Quantity = source.Quantity;
                    line.SourceLocationId = source.DestinationLocationId;
                    line.DestinationLocationId = source.SourceLocationId;
                    break;
                case InventoryMovementType.Adjustment:
                    break;
            }

            foreach (var change in source.BalanceChanges)
            {
                var lotId = change.LotId ?? legacyLots[source.ProductId].Id;
                var balance = balances[new(source.ProductId, change.LocationId, lotId)];
                var previous = balance.Quantity;
                var delta = -change.DeltaQuantity;
                var resulting = previous + delta;
                if (decimal.Round(resulting, 4) != resulting ||
                    Math.Abs(resulting) > InventoryMovementRules.MaximumQuantity)
                    throw new InvalidOperationException("El reverso excede la precisión permitida del saldo.");

                line.BalanceChanges.Add(new InventoryBalanceChange
                {
                    LocationId = change.LocationId,
                    LotId = lotId,
                    LotNumberSnapshot = change.LotNumberSnapshot ?? legacyLots.GetValueOrDefault(source.ProductId)?.Number,
                    LotDateSnapshot = change.LotDateSnapshot ?? legacyLots.GetValueOrDefault(source.ProductId)?.LotDate,
                    DeltaQuantity = delta,
                    PreviousQuantity = previous,
                    ResultingQuantity = resulting
                });
                balance.Quantity = resulting;
                balance.UpdatedAt = now;
                if (original.Type == InventoryMovementType.Adjustment)
                {
                    line.PreviousQuantity = previous;
                    line.AdjustmentDelta = delta;
                    line.Quantity = resulting;
                }
            }

            reversal.Lines.Add(line);
        }

        dbContext.InventoryMovements.Add(reversal);
        return reversal;
    }

    internal async Task<IReadOnlyList<InventoryBalanceResult>> CurrentBalancesAsync(
        InventoryMovement movement,
        CancellationToken cancellationToken)
    {
        var keys = movement.Lines.SelectMany(line => line.BalanceChanges.Select(change =>
                new InventoryBalanceKey(line.ProductId, change.LocationId, change.LotId)))
            .Distinct()
            .ToArray();
        var products = keys.Select(key => key.ProductId).ToArray();
        var locations = keys.Select(key => key.LocationId).ToArray();
        return await dbContext.InventoryBalances.AsNoTracking()
            .Where(item => products.Contains(item.ProductId) && locations.Contains(item.LocationId))
            .Select(item => new InventoryBalanceResult(
                item.ProductId,
                item.LocationId,
                item.LotId,
                item.Quantity,
                item.Version,
                item.Quantity < 0))
            .ToListAsync(cancellationToken);
    }

    private static InventoryMovementType ReverseType(InventoryMovementType type) => type switch
    {
        InventoryMovementType.Entry => InventoryMovementType.Exit,
        InventoryMovementType.Exit => InventoryMovementType.Entry,
        InventoryMovementType.Transfer => InventoryMovementType.Transfer,
        InventoryMovementType.Adjustment => InventoryMovementType.Adjustment,
        _ => throw new InvalidOperationException()
    };
}

internal static class InventoryFingerprint
{
    internal static string Hash(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
