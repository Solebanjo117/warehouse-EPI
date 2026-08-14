using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
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

public sealed record InventoryPositionView(
    Guid ProductId,
    string ProductSku,
    string? ProductDescription,
    string UnitCode,
    bool ProductIsActive,
    Guid LocationId,
    string LocationCode,
    string? LocationDescription,
    bool LocationIsActive,
    bool LocationIsBlocked,
    decimal Quantity,
    bool HasActiveAssignment,
    bool HasNonZeroBalance)
{
    public bool IsNegative => Quantity < 0;
}

public sealed class InventoryQueryService(WarehouseDbContext dbContext)
{
    public async Task<InventoryBalanceSnapshot> GetBalanceAsync(
        Guid productId,
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(candidate => candidate.ProductId == productId && candidate.LocationId == locationId)
            .Select(candidate => new { candidate.LotId, candidate.Quantity, candidate.Version })
            .ToListAsync(cancellationToken);
        if (balances.Count == 0) return new(productId, locationId, 0m, 0, false, false);
        var total = balances.Sum(item => item.Quantity);
        var text = string.Join('|', balances.OrderBy(item => item.LotId).Select(item =>
            $"{item.LotId}:{item.Quantity.ToString("G29", CultureInfo.InvariantCulture)}:{item.Version}"));
        var version = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
        return new(productId, locationId, total, version, true, total < 0);
    }

    public async Task<IReadOnlyList<InventoryBalanceView>> GetProductBalancesAsync(
        Guid productId,
        CancellationToken cancellationToken = default) =>
        await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.ProductId == productId)
            .OrderBy(balance => balance.Location.Code)
            .Select(ToBalanceView())
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InventoryBalanceView>> GetLocationContentsAsync(
        Guid locationId,
        CancellationToken cancellationToken = default) =>
        await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.LocationId == locationId)
            .OrderBy(balance => balance.Product.Sku)
            .Select(ToBalanceView())
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InventoryPositionView>> GetProductInventoryAsync(
        Guid productId,
        CancellationToken cancellationToken = default)
    {
        var assignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(assignment => assignment.ProductId == productId && assignment.IsActive)
            .Select(ToPositionFromAssignment())
            .ToListAsync(cancellationToken);
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.ProductId == productId && balance.Quantity != 0)
            .Select(ToPositionFromBalance())
            .ToListAsync(cancellationToken);

        return MergePositions(assignments, balances, item => item.LocationCode);
    }

    public async Task<IReadOnlyList<InventoryPositionView>> GetLocationInventoryAsync(
        Guid locationId,
        CancellationToken cancellationToken = default)
    {
        var assignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(assignment => assignment.LocationId == locationId && assignment.IsActive)
            .Select(ToPositionFromAssignment())
            .ToListAsync(cancellationToken);
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.LocationId == locationId && balance.Quantity != 0)
            .Select(ToPositionFromBalance())
            .ToListAsync(cancellationToken);

        return MergePositions(assignments, balances, item => item.ProductSku);
    }

    public async Task<decimal> GetProductTotalAsync(
        Guid productId,
        CancellationToken cancellationToken = default) =>
        await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.ProductId == productId)
            .SumAsync(balance => (decimal?)balance.Quantity, cancellationToken) ?? 0m;

    public async Task<IReadOnlyList<InventoryBalanceView>> GetNegativeBalancesAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.Quantity < 0)
            .OrderBy(balance => balance.Product.Sku)
            .ThenBy(balance => balance.Location.Code)
            .Select(ToBalanceView())
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

    private static System.Linq.Expressions.Expression<Func<InventoryBalance, InventoryBalanceView>> ToBalanceView() =>
        balance => new InventoryBalanceView(
                balance.Id,
                balance.ProductId,
                balance.Product.Sku,
                balance.LocationId,
                balance.Location.Code,
                balance.LotId,
                balance.Lot == null ? null : balance.Lot.Number,
                balance.Quantity,
                balance.Version,
                balance.Quantity < 0);

    private static System.Linq.Expressions.Expression<Func<ProductLocationAssignment, InventoryPositionView>>
        ToPositionFromAssignment() =>
        assignment => new(
            assignment.ProductId,
            assignment.Product.Sku,
            assignment.Product.Description,
            assignment.Product.BaseUnit.Code,
            assignment.Product.IsActive,
            assignment.LocationId,
            assignment.Location.Code,
            assignment.Location.Description,
            assignment.Location.IsActive,
            assignment.Location.IsBlocked,
            0m,
            true,
            false);

    private static System.Linq.Expressions.Expression<Func<InventoryBalance, InventoryPositionView>>
        ToPositionFromBalance() =>
        balance => new(
            balance.ProductId,
            balance.Product.Sku,
            balance.Product.Description,
            balance.Product.BaseUnit.Code,
            balance.Product.IsActive,
            balance.LocationId,
            balance.Location.Code,
            balance.Location.Description,
            balance.Location.IsActive,
            balance.Location.IsBlocked,
            balance.Quantity,
            false,
            true);

    private static IReadOnlyList<InventoryPositionView> MergePositions(
        IEnumerable<InventoryPositionView> assignments,
        IEnumerable<InventoryPositionView> balances,
        Func<InventoryPositionView, string> orderBy)
    {
        var positions = assignments.ToDictionary(item => (item.ProductId, item.LocationId));
        foreach (var balance in balances)
        {
            var key = (balance.ProductId, balance.LocationId);
            positions[key] = positions.TryGetValue(key, out var existing)
                ? existing with { Quantity = existing.Quantity + balance.Quantity }
                : balance;
        }

        return positions.Values
            .Where(item => item.HasActiveAssignment || item.Quantity != 0)
            .Select(item => item with { HasNonZeroBalance = item.Quantity != 0 })
            .OrderBy(orderBy, StringComparer.Ordinal)
            .ToArray();
    }
}
