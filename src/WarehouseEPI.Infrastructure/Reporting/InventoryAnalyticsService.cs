using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>Consultas analíticas de ocupación, rotación y estancamiento.</summary>
public sealed class InventoryAnalyticsService(
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService)
{
    public async Task<LocationOccupancyReportDto> GetOccupancyAsync(
        CancellationToken cancellationToken = default)
    {
        var locations = await dbContext.Locations
            .AsNoTracking()
            .Where(location =>
                location.Kind == LocationKind.Rack &&
                location.OperationalRole == LocationOperationalRole.Storage)
            .Select(location => new OccupancyLocation(
                location.Id,
                location.RowCode ?? "SIN-FILA",
                location.IsActive,
                location.IsBlocked))
            .ToListAsync(cancellationToken);

        var locationIds = locations.Select(location => location.Id).ToArray();
        var balanceRows = locationIds.Length == 0
            ? new List<OccupancyBalance>()
            : await dbContext.InventoryBalances
                .AsNoTracking()
                .Where(balance => locationIds.Contains(balance.LocationId))
                .GroupBy(balance => new { balance.LocationId, balance.ProductId })
                .Select(balances => new OccupancyBalance(
                    balances.Key.LocationId,
                    balances.Sum(balance => balance.Quantity)))
                .ToListAsync(cancellationToken);
        var balancesByLocation = balanceRows.ToLookup(balance => balance.LocationId);
        var states = locations
            .Select(location => new OccupancyState(
                location.RowCode,
                Classify(location, balancesByLocation[location.Id])))
            .ToArray();

        var rows = states
            .GroupBy(state => state.RowCode, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new LocationOccupancyRowDto(group.Key, Summarize(group.Select(state => state.State))))
            .ToArray();
        return new(Summarize(states.Select(state => state.State)), rows);
    }

    public async Task<InventoryAnalyticsPage<SkuRotationMetricDto>> GetRotationPageAsync(
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var rows = await BuildRotationAsync(normalized, cancellationToken);
        return Page(rows, normalized.PageNumber, normalized.PageSize);
    }

    public async Task<InventoryAnalyticsExportBatch<SkuRotationMetricDto>> GetRotationExportAsync(
        InventoryAnalyticsFilter filter,
        int maximumRows = 10000,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var limit = Math.Clamp(maximumRows, 1, 50000);
        var products = await LoadProductsAsync(normalized, cancellationToken);
        if (products.Count > limit)
            return new([], products.Count, limit);

        return new(await BuildRotationAsync(normalized, cancellationToken, products), products.Count, limit);
    }

    public async Task<InventoryAnalyticsPage<StagnantProductDto>> GetStagnantPageAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var rows = await BuildStagnantAsync(normalized, nowUtc, cancellationToken);
        return Page(rows, normalized.PageNumber, normalized.PageSize);
    }

    public async Task<InventoryAnalyticsExportBatch<StagnantProductDto>> GetStagnantExportAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        int maximumRows = 10000,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var rows = await BuildStagnantAsync(normalized, nowUtc, cancellationToken);
        var limit = Math.Clamp(maximumRows, 1, 50000);
        return rows.Count > limit
            ? new([], rows.Count, limit)
            : new(rows, rows.Count, limit);
    }

    private async Task<IReadOnlyList<SkuRotationMetricDto>> BuildRotationAsync(
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken,
        IReadOnlyList<ProductProjection>? loadedProducts = null)
    {
        var products = loadedProducts ?? await LoadProductsAsync(filter, cancellationToken);
        var productIds = products.Select(product => product.Id).ToArray();
        if (productIds.Length == 0)
            return [];

        var currentStock = await LoadCurrentStockAsync(productIds, cancellationToken);
        var rankingQuery = EffectiveExitLines(productIds);
        if (filter.FromUtc is not null)
            rankingQuery = rankingQuery.Where(line => line.Movement.OccurredAt >= filter.FromUtc.Value);
        if (filter.ToUtc is not null)
            rankingQuery = rankingQuery.Where(line => line.Movement.OccurredAt < filter.ToUtc.Value);

        var ranking = await rankingQuery
            .GroupBy(line => line.ProductId)
            .Select(lines => new ExitRankingProjection(
                lines.Key,
                lines.Select(line => line.MovementId).Distinct().Count(),
                lines.Sum(line => line.Quantity)))
            .ToListAsync(cancellationToken);
        var rankingByProduct = ranking.ToDictionary(row => row.ProductId);
        var lastExits = await LoadLastExitsAsync(productIds, cancellationToken);

        return products
            .Select(product =>
            {
                var metric = rankingByProduct.GetValueOrDefault(product.Id);
                return new SkuRotationMetricDto(
                    product.Id,
                    product.Sku,
                    product.Description,
                    product.UnitId,
                    product.UnitCode,
                    metric?.MovementCount ?? 0,
                    metric?.Quantity ?? 0m,
                    currentStock.GetValueOrDefault(product.Id),
                    lastExits.GetValueOrDefault(product.Id),
                    product.IsActive);
            })
            .OrderByDescending(row => row.EffectiveExitMovementCount)
            .ThenByDescending(row => row.QuantityInBaseUnit)
            .ThenBy(row => row.Sku, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<StagnantProductDto>> BuildStagnantAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var products = await LoadProductsAsync(filter, cancellationToken);
        var productIds = products.Select(product => product.Id).ToArray();
        if (productIds.Length == 0)
            return [];

        var currentStock = await LoadCurrentStockAsync(productIds, cancellationToken);
        var candidates = products.Where(product => currentStock.GetValueOrDefault(product.Id) > 0m).ToArray();
        if (candidates.Length == 0)
            return [];

        var candidateIds = candidates.Select(product => product.Id).ToArray();
        var lastExits = await LoadLastExitsAsync(candidateIds, cancellationToken);
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var warehouseDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);

        return candidates
            .Select(product =>
            {
                var lastExitUtc = lastExits.GetValueOrDefault(product.Id);
                var days = lastExitUtc is null
                    ? (int?)null
                    : warehouseDate.DayNumber - DateOnly.FromDateTime(
                        TimeZoneInfo.ConvertTime(lastExitUtc.Value, timeZone).DateTime).DayNumber;
                var category = Category(days);
                return category is null
                    ? null
                    : new StagnantProductDto(
                        product.Id,
                        product.Sku,
                        product.Description,
                        product.UnitId,
                        product.UnitCode,
                        currentStock[product.Id],
                        lastExitUtc,
                        days,
                        category.Value,
                        product.IsActive);
            })
            .OfType<StagnantProductDto>()
            .OrderBy(row => CategoryPriority(row.Category))
            .ThenByDescending(row => row.DaysWithoutExit)
            .ThenBy(row => row.Sku, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<ProductProjection>> LoadProductsAsync(
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Products.AsNoTracking().AsQueryable();
        query = filter.ProductStatus switch
        {
            "inactive" => query.Where(product => !product.IsActive),
            "all" => query,
            _ => query.Where(product => product.IsActive)
        };
        if (filter.UnitId is not null)
            query = query.Where(product => product.BaseUnitId == filter.UnitId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            query = query.Where(product =>
                product.Sku.ToUpper().Contains(term) ||
                (product.Description != null && product.Description.ToUpper().Contains(term)) ||
                (product.ExternalReference != null && product.ExternalReference.ToUpper().Contains(term)));
        }

        return await query
            .OrderBy(product => product.Sku)
            .Select(product => new ProductProjection(
                product.Id,
                product.Sku,
                product.Description,
                product.BaseUnitId,
                product.BaseUnit.Code,
                product.IsActive))
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, decimal>> LoadCurrentStockAsync(
        Guid[] productIds,
        CancellationToken cancellationToken) =>
        await dbContext.InventoryBalances
            .AsNoTracking()
            .Where(balance => productIds.Contains(balance.ProductId))
            .GroupBy(balance => balance.ProductId)
            .Select(balances => new { ProductId = balances.Key, Quantity = balances.Sum(balance => balance.Quantity) })
            .ToDictionaryAsync(row => row.ProductId, row => row.Quantity, cancellationToken);

    private async Task<Dictionary<Guid, DateTimeOffset?>> LoadLastExitsAsync(
        Guid[] productIds,
        CancellationToken cancellationToken) =>
        await EffectiveExitLines(productIds)
            .GroupBy(line => line.ProductId)
            .Select(lines => new
            {
                ProductId = lines.Key,
                LastExit = (DateTimeOffset?)lines.Max(line => line.Movement.OccurredAt)
            })
            .ToDictionaryAsync(row => row.ProductId, row => row.LastExit, cancellationToken);

    private IQueryable<InventoryMovementLine> EffectiveExitLines(Guid[] productIds) =>
        dbContext.InventoryMovements
            .AsNoTracking()
            .WhereEffective(dbContext)
            .Where(movement => movement.Type == InventoryMovementType.Exit)
            .SelectMany(movement => movement.Lines)
            .Where(line => productIds.Contains(line.ProductId))
            .AsQueryable();

    private static InventoryAnalyticsFilter Normalize(InventoryAnalyticsFilter filter) => filter with
    {
        ProductStatus = filter.ProductStatus is "inactive" or "all" ? filter.ProductStatus : "active",
        Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(),
        PageNumber = Math.Max(1, filter.PageNumber),
        PageSize = Math.Clamp(filter.PageSize, 1, 100)
    };

    private static InventoryAnalyticsPage<T> Page<T>(
        IReadOnlyList<T> rows,
        int requestedPage,
        int pageSize)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)pageSize));
        var page = Math.Clamp(requestedPage, 1, totalPages);
        return new(
            rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
            rows.Count,
            page,
            pageSize);
    }

    private static LocationOccupancySummaryDto Summarize(IEnumerable<OccupancyKind> states)
    {
        var values = states.ToArray();
        return new(
            values.Length,
            values.Count(state => state == OccupancyKind.Occupied),
            values.Count(state => state == OccupancyKind.Empty),
            values.Count(state => state == OccupancyKind.Negative),
            values.Count(state => state == OccupancyKind.Blocked),
            values.Count(state => state == OccupancyKind.Inactive));
    }

    private static OccupancyKind Classify(
        OccupancyLocation location,
        IEnumerable<OccupancyBalance> balances)
    {
        if (!location.IsActive)
            return OccupancyKind.Inactive;
        if (location.IsBlocked)
            return OccupancyKind.Blocked;
        var quantities = balances.Select(balance => balance.Quantity).ToArray();
        if (quantities.Any(quantity => quantity < 0m))
            return OccupancyKind.Negative;
        return quantities.Any(quantity => quantity > 0m)
            ? OccupancyKind.Occupied
            : OccupancyKind.Empty;
    }

    private static StagnantCategory? Category(int? days) => days switch
    {
        null => StagnantCategory.NeverExited,
        >= 90 => StagnantCategory.Days90Plus,
        >= 60 => StagnantCategory.Days60To89,
        >= 30 => StagnantCategory.Days30To59,
        _ => null
    };

    private static int CategoryPriority(StagnantCategory category) => category switch
    {
        StagnantCategory.NeverExited => 0,
        StagnantCategory.Days90Plus => 1,
        StagnantCategory.Days60To89 => 2,
        _ => 3
    };

    private enum OccupancyKind { Inactive, Blocked, Negative, Occupied, Empty }
    private sealed record OccupancyLocation(Guid Id, string RowCode, bool IsActive, bool IsBlocked);
    private sealed record OccupancyBalance(Guid LocationId, decimal Quantity);
    private sealed record OccupancyState(string RowCode, OccupancyKind State);
    private sealed record ProductProjection(
        Guid Id,
        string Sku,
        string? Description,
        short UnitId,
        string UnitCode,
        bool IsActive);
    private sealed record ExitRankingProjection(Guid ProductId, int MovementCount, decimal Quantity);
}
