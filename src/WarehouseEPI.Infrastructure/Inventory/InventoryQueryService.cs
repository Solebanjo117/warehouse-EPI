using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
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

public enum InventoryPositionFilter
{
    All,
    WithBalance,
    Negative,
    AssignedZero,
    UnassignedBalance
}

public sealed record InventoryPositionSummary(
    int Positions,
    int WithBalance,
    int Negative,
    int ActiveAssignments,
    int UnassignedBalances);

public sealed record InventoryPositionPage(
    IReadOnlyList<InventoryPositionView> Items,
    int TotalCount,
    int PageNumber,
    int PageSize,
    InventoryPositionSummary Summary);

public sealed record InventoryAlertSummary(
    int NegativePositions,
    int NegativeProducts,
    int BelowMinimumProducts);

public sealed record NegativeInventoryAlert(
    Guid ProductId,
    string ProductSku,
    string? ProductDescription,
    string UnitCode,
    Guid LocationId,
    string LocationCode,
    string? LocationDescription,
    decimal Quantity);

public sealed record MinimumStockInventoryAlert(
    Guid ProductId,
    string Sku,
    string? Description,
    string UnitCode,
    decimal TotalQuantity,
    decimal MinimumStock)
{
    public decimal Deficit => MinimumStock - TotalQuantity;
    public decimal? CoveragePercent => MinimumStock > 0 ? TotalQuantity / MinimumStock * 100m : null;
}

public sealed record InventoryAlertPage<T>(IReadOnlyList<T> Items, int TotalCount);

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

    public async Task<InventoryPositionPage> GetProductInventoryPageAsync(
        Guid productId,
        InventoryPositionFilter filter,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        CreatePositionPage(await GetProductInventoryAsync(productId, cancellationToken), filter, pageNumber, pageSize);

    public async Task<InventoryPositionPage> GetLocationInventoryPageAsync(
        Guid locationId,
        InventoryPositionFilter filter,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        CreatePositionPage(await GetLocationInventoryAsync(locationId, cancellationToken), filter, pageNumber, pageSize);

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

    public async Task<InventoryAlertSummary> GetAlertSummaryAsync(CancellationToken cancellationToken = default)
    {
        var negatives = dbContext.InventoryBalances.AsNoTracking().Where(balance => balance.Quantity < 0);
        var negativePositions = await negatives.CountAsync(cancellationToken);
        var negativeProducts = await negatives.Select(balance => balance.ProductId).Distinct().CountAsync(cancellationToken);
        var belowMinimum = await (
            from product in dbContext.Products.AsNoTracking()
            where product.IsActive
            join balance in dbContext.InventoryBalances.AsNoTracking()
                on product.Id equals balance.ProductId into balances
            let total = balances.Sum(balance => (decimal?)balance.Quantity) ?? 0m
            where total < product.MinimumStock
            select product.Id).CountAsync(cancellationToken);
        return new(negativePositions, negativeProducts, belowMinimum);
    }

    public async Task<InventoryAlertPage<NegativeInventoryAlert>> GetNegativeAlertPageAsync(
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var normalized = CatalogNormalization.NormalizeCode(search ?? string.Empty);
        var query = dbContext.InventoryBalances.AsNoTracking().Where(balance => balance.Quantity < 0);
        if (normalized.Length > 0)
            query = query.Where(balance => balance.Product.Sku.Contains(normalized) ||
                (balance.Product.Description != null && balance.Product.Description.ToUpper().Contains(normalized)) ||
                balance.Location.Code.Contains(normalized) ||
                (balance.Location.Description != null && balance.Location.Description.ToUpper().Contains(normalized)));
        var totalCount = await query.CountAsync(cancellationToken);
        var page = Math.Min(NormalizePage(pageNumber), Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize)));
        var items = await query.OrderBy(balance => balance.Product.Sku).ThenBy(balance => balance.Location.Code)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(balance => new NegativeInventoryAlert(
                balance.ProductId,
                balance.Product.Sku,
                balance.Product.Description,
                balance.Product.BaseUnit.Code,
                balance.LocationId,
                balance.Location.Code,
                balance.Location.Description,
                balance.Quantity))
            .ToListAsync(cancellationToken);
        return new(items, totalCount);
    }

    public async Task<InventoryAlertPage<MinimumStockInventoryAlert>> GetBelowMinimumAlertPageAsync(
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var normalized = CatalogNormalization.NormalizeCode(search ?? string.Empty);
        var query =
            from product in dbContext.Products.AsNoTracking()
            where product.IsActive
            join balance in dbContext.InventoryBalances.AsNoTracking()
                on product.Id equals balance.ProductId into balances
            let total = balances.Sum(balance => (decimal?)balance.Quantity) ?? 0m
            where total < product.MinimumStock
            select new { product, total };
        if (normalized.Length > 0)
            query = query.Where(item => item.product.Sku.Contains(normalized) ||
                (item.product.Description != null && item.product.Description.ToUpper().Contains(normalized)));
        var totalCount = await query.CountAsync(cancellationToken);
        var page = Math.Min(NormalizePage(pageNumber), Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize)));
        var items = await query.OrderBy(item => item.product.Sku)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new MinimumStockInventoryAlert(
                item.product.Id,
                item.product.Sku,
                item.product.Description,
                item.product.BaseUnit.Code,
                item.total,
                item.product.MinimumStock))
            .ToListAsync(cancellationToken);
        return new(items, totalCount);
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

    private static InventoryPositionPage CreatePositionPage(
        IReadOnlyList<InventoryPositionView> positions,
        InventoryPositionFilter filter,
        int pageNumber,
        int pageSize)
    {
        var summary = new InventoryPositionSummary(
            positions.Count,
            positions.Count(item => item.HasNonZeroBalance),
            positions.Count(item => item.IsNegative),
            positions.Count(item => item.HasActiveAssignment),
            positions.Count(item => item.HasNonZeroBalance && !item.HasActiveAssignment));
        var filtered = positions.Where(item => filter switch
        {
            InventoryPositionFilter.WithBalance => item.HasNonZeroBalance,
            InventoryPositionFilter.Negative => item.IsNegative,
            InventoryPositionFilter.AssignedZero => item.HasActiveAssignment && item.Quantity == 0,
            InventoryPositionFilter.UnassignedBalance => item.HasNonZeroBalance && !item.HasActiveAssignment,
            _ => true
        }).ToArray();
        var normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)normalizedPageSize));
        var page = Math.Clamp(Math.Max(1, pageNumber), 1, totalPages);
        return new(filtered.Skip((page - 1) * normalizedPageSize).Take(normalizedPageSize).ToArray(), filtered.Length,
            page, normalizedPageSize, summary);
    }

    private static int NormalizePage(int pageNumber) => Math.Max(1, pageNumber);
}
