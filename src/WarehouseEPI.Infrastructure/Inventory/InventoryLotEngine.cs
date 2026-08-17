using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

internal sealed class InventoryLotEngine(WarehouseDbContext dbContext)
{
    internal async Task<Dictionary<Guid, List<ProductLot>>> GetOrCreateDailyLotsAsync(
        IEnumerable<Product> products,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var lotDate = GetWarehouseDate(now);
        var lots = new Dictionary<Guid, List<ProductLot>>();
        foreach (var product in products)
            lots[product.Id] = await GetOrCreateDailyLotAsync(product.Id, lotDate, now, cancellationToken);
        return lots;
    }

    internal static DateOnly GetWarehouseDate(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now,
            TimeZoneInfo.FindSystemTimeZoneById("America/Matamoros")).DateTime);

    internal static string DailyLotNumber(DateOnly lotDate) => $"AUTO-{lotDate:yyyyMMdd}";

    internal static uint AggregateVersion(IEnumerable<InventoryBalance> balances)
    {
        var text = string.Join('|', balances.OrderBy(item => item.LotId).Select(item =>
            $"{item.LotId:N}:{item.Quantity.ToString("G29", CultureInfo.InvariantCulture)}:{item.Version}"));
        return text.Length == 0 ? 0 : BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
    }

    internal static void ApplyTrackedLine(
        InventoryMovementType type,
        InventoryMovementLineCommand command,
        InventoryMovementLine line,
        IReadOnlyDictionary<InventoryBalanceKey, InventoryBalance> balances,
        IReadOnlyList<ProductLot> lots,
        ProductLot daily,
        DateTimeOffset now)
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
                if (balance.Quantity <= 0)
                    continue;

                var take = Math.Min(remaining, balance.Quantity);
                if (take > 0)
                {
                    remaining -= take;
                    last = lot;
                    yield return (balance, lot, -take);
                }

                if (remaining == 0)
                    yield break;
            }

            var fallback = last ?? ordered.FirstOrDefault() ?? daily;
            if (remaining > 0)
                yield return (Balance(location, fallback), fallback, -remaining);
        }

        switch (type)
        {
            case InventoryMovementType.Entry:
                Add(Balance(command.DestinationLocationId!.Value, daily), daily, command.Quantity);
                break;
            case InventoryMovementType.Exit:
                foreach (var change in Consume(command.SourceLocationId!.Value, command.Quantity))
                    Add(change.Balance, change.Lot, change.Delta);
                break;
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
                line.PreviousQuantity = current;
                line.AdjustmentDelta = delta;
                if (delta >= 0)
                    Add(Balance(command.LocationId!.Value, daily), daily, delta);
                else
                    foreach (var change in Consume(command.LocationId!.Value, -delta))
                        Add(change.Balance, change.Lot, change.Delta);
                break;
            default:
                throw new InvalidOperationException("Tipo de movimiento no soportado.");
        }
    }

    private async Task<List<ProductLot>> GetOrCreateDailyLotAsync(
        Guid productId,
        DateOnly lotDate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var number = DailyLotNumber(lotDate);
        var existing = await dbContext.ProductLots.Where(item => item.ProductId == productId).ToListAsync(cancellationToken);
        if (existing.All(item => item.NormalizedNumber != number))
        {
            existing.Add(new ProductLot
            {
                ProductId = productId,
                Number = number,
                NormalizedNumber = number,
                LotDate = lotDate,
                CreatedAt = now
            });
            dbContext.ProductLots.Add(existing[^1]);
        }

        return existing;
    }

    private static void ApplyChange(
        InventoryMovementLine line,
        InventoryBalance balance,
        decimal delta,
        DateTimeOffset now,
        ProductLot lot)
    {
        var previous = balance.Quantity;
        var resulting = previous + delta;
        if (Math.Abs(resulting) > InventoryMovementRules.MaximumQuantity || decimal.Round(resulting, 4) != resulting)
            throw new InventoryQuantityOutOfRangeException(
                "El saldo resultante excede la precisión numeric(18,4).");

        line.BalanceChanges.Add(new InventoryBalanceChange
        {
            LocationId = balance.LocationId,
            LotId = balance.LotId,
            LotNumberSnapshot = lot.Number,
            LotDateSnapshot = lot.LotDate,
            DeltaQuantity = delta,
            PreviousQuantity = previous,
            ResultingQuantity = resulting
        });
        balance.Quantity = resulting;
        balance.UpdatedAt = now;
    }
}
