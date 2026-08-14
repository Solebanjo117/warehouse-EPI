using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record InventoryBalanceView(
    Guid BalanceId,
    Guid ProductId,
    string ProductSku,
    Guid LocationId,
    string LocationCode,
    Guid? LotId,
    string? LotNumber,
    decimal Quantity,
    uint Version,
    bool IsNegative);

public sealed record ProductStockSummary(
    Guid ProductId,
    string Sku,
    decimal TotalQuantity,
    decimal MinimumStock,
    bool IsBelowMinimum);

public sealed record InventoryBalanceSnapshot(
    Guid ProductId,
    Guid LocationId,
    decimal Quantity,
    uint Version,
    bool Exists,
    bool IsNegative);

public sealed class InventoryQueryService(WarehouseDbContext dbContext)
{
    public async Task<InventoryBalanceSnapshot> GetBalanceAsync(
        Guid productId,
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        var balance = await dbContext.InventoryBalances.AsNoTracking()
            .Where(candidate => candidate.ProductId == productId &&
                candidate.LocationId == locationId && candidate.LotId == null)
            .Select(candidate => new InventoryBalanceSnapshot(
                productId,
                locationId,
                candidate.Quantity,
                candidate.Version,
                true,
                candidate.Quantity < 0))
            .SingleOrDefaultAsync(cancellationToken);

        return balance ?? new(productId, locationId, 0m, 0, false, false);
    }

    public async Task<IReadOnlyList<InventoryBalanceView>> GetProductBalancesAsync(
        Guid productId,
        CancellationToken cancellationToken = default) =>
        await QueryBalances().Where(balance => balance.ProductId == productId)
            .OrderBy(balance => balance.LocationCode)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InventoryBalanceView>> GetLocationContentsAsync(
        Guid locationId,
        CancellationToken cancellationToken = default) =>
        await QueryBalances().Where(balance => balance.LocationId == locationId)
            .OrderBy(balance => balance.ProductSku)
            .ToListAsync(cancellationToken);

    public async Task<decimal> GetProductTotalAsync(
        Guid productId,
        CancellationToken cancellationToken = default) =>
        await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.ProductId == productId)
            .SumAsync(balance => (decimal?)balance.Quantity, cancellationToken) ?? 0m;

    public async Task<IReadOnlyList<InventoryBalanceView>> GetNegativeBalancesAsync(
        CancellationToken cancellationToken = default) =>
        await QueryBalances().Where(balance => balance.Quantity < 0)
            .OrderBy(balance => balance.ProductSku)
            .ThenBy(balance => balance.LocationCode)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProductStockSummary>> GetBelowMinimumProductsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await (
            from product in dbContext.Products.AsNoTracking()
            where product.IsActive
            join balance in dbContext.InventoryBalances.AsNoTracking()
                on product.Id equals balance.ProductId into balances
            let total = balances.Sum(balance => (decimal?)balance.Quantity) ?? 0m
            where total < product.MinimumStock
            orderby product.Sku
            select new
            {
                product.Id,
                product.Sku,
                Total = total,
                product.MinimumStock
            }).ToListAsync(cancellationToken);

        return rows.Select(row => new ProductStockSummary(
            row.Id,
            row.Sku,
            row.Total,
            row.MinimumStock,
            true)).ToArray();
    }

    private IQueryable<InventoryBalanceView> QueryBalances() =>
        dbContext.InventoryBalances.AsNoTracking()
            .Select(balance => new InventoryBalanceView(
                balance.Id,
                balance.ProductId,
                balance.Product.Sku,
                balance.LocationId,
                balance.Location.Code,
                balance.LotId,
                balance.Lot == null ? null : balance.Lot.Number,
                balance.Quantity,
                balance.Version,
                balance.Quantity < 0));
}
