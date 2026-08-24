using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>Consultas analíticas de ocupación, actividad de salidas y estancamiento.</summary>
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

    public async Task<InventoryAnalyticsPage<SkuExitActivityMetricDto>> GetExitActivityPageAsync(
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var products = ApplyProductFilter(normalized);
        var totalCount = await products.CountAsync(cancellationToken);
        var pageNumber = NormalizePage(normalized.PageNumber, normalized.PageSize, totalCount);
        var rows = await ProjectExitActivity(OrderExitActivity(products, normalized)
            .Skip((pageNumber - 1) * normalized.PageSize)
            .Take(normalized.PageSize), normalized)
            .ToListAsync(cancellationToken);
        return new(rows, totalCount, pageNumber, normalized.PageSize);
    }

    public async Task<InventoryAnalyticsExportBatch<SkuExitActivityMetricDto>> GetExitActivityExportAsync(
        InventoryAnalyticsFilter filter,
        int maximumRows = 10000,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var limit = Math.Clamp(maximumRows, 1, 50000);
        var products = ApplyProductFilter(normalized);
        var totalCount = await products.CountAsync(cancellationToken);
        if (totalCount > limit)
            return new([], totalCount, limit);

        var rows = await ProjectExitActivity(OrderExitActivity(products, normalized), normalized)
            .ToListAsync(cancellationToken);
        return new(rows, totalCount, limit);
    }

    public async Task<InventoryAnalyticsPage<StagnantProductDto>> GetStagnantPageAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var context = await BuildStagnantQueryAsync(normalized, nowUtc, cancellationToken);
        var totalCount = await context.Products.CountAsync(cancellationToken);
        var pageNumber = NormalizePage(normalized.PageNumber, normalized.PageSize, totalCount);
        var rows = await ProjectStagnant(OrderStagnant(context.Products)
            .Skip((pageNumber - 1) * normalized.PageSize)
            .Take(normalized.PageSize))
            .ToListAsync(cancellationToken);
        return new(
            rows.Select(row => ToStagnantDto(row, context.WarehouseDate, context.TimeZone)).ToArray(),
            totalCount,
            pageNumber,
            normalized.PageSize);
    }

    public async Task<InventoryAnalyticsExportBatch<StagnantProductDto>> GetStagnantExportAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        int maximumRows = 10000,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var limit = Math.Clamp(maximumRows, 1, 50000);
        var context = await BuildStagnantQueryAsync(normalized, nowUtc, cancellationToken);
        var totalCount = await context.Products.CountAsync(cancellationToken);
        if (totalCount > limit)
            return new([], totalCount, limit);

        var rows = await ProjectStagnant(OrderStagnant(context.Products))
            .ToListAsync(cancellationToken);
        return new(
            rows.Select(row => ToStagnantDto(row, context.WarehouseDate, context.TimeZone)).ToArray(),
            totalCount,
            limit);
    }

    private IQueryable<Product> OrderExitActivity(
        IQueryable<Product> products,
        InventoryAnalyticsFilter filter)
    {
        var rankingLines = EffectiveExitLines();
        if (filter.FromUtc is not null)
            rankingLines = rankingLines.Where(line => line.Movement.OccurredAt >= filter.FromUtc.Value);
        if (filter.ToUtc is not null)
            rankingLines = rankingLines.Where(line => line.Movement.OccurredAt < filter.ToUtc.Value);
        return products
            .OrderByDescending(product => rankingLines
                .Where(line => line.ProductId == product.Id)
                .Select(line => line.MovementId)
                .Distinct()
                .Count())
            .ThenByDescending(product => rankingLines
                .Where(line => line.ProductId == product.Id)
                .Sum(line => (decimal?)line.Quantity) ?? 0m)
            .ThenBy(product => product.Sku);
    }

    private IQueryable<SkuExitActivityMetricDto> ProjectExitActivity(
        IQueryable<Product> products,
        InventoryAnalyticsFilter filter)
    {
        var rankingLines = EffectiveExitLines();
        if (filter.FromUtc is not null)
            rankingLines = rankingLines.Where(line => line.Movement.OccurredAt >= filter.FromUtc.Value);
        if (filter.ToUtc is not null)
            rankingLines = rankingLines.Where(line => line.Movement.OccurredAt < filter.ToUtc.Value);
        var allExitLines = EffectiveExitLines();
        return products.Select(product => new SkuExitActivityMetricDto(
                product.Id,
                product.Sku,
                product.Description,
                product.BaseUnitId,
                product.BaseUnit.Code,
                rankingLines
                    .Where(line => line.ProductId == product.Id)
                    .Select(line => line.MovementId)
                    .Distinct()
                    .Count(),
                rankingLines
                    .Where(line => line.ProductId == product.Id)
                    .Sum(line => (decimal?)line.Quantity) ?? 0m,
                dbContext.InventoryBalances
                    .Where(balance => balance.ProductId == product.Id)
                    .Sum(balance => (decimal?)balance.Quantity) ?? 0m,
                allExitLines
                    .Where(line => line.ProductId == product.Id)
                    .Max(line => (DateTimeOffset?)line.Movement.OccurredAt),
                product.IsActive));
    }

    private async Task<StagnantQueryContext> BuildStagnantQueryAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var warehouseDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);
        var staleBeforeUtc = ToUtcStart(warehouseDate.AddDays(-29), timeZone);
        var products = ApplyProductFilter(filter);
        var allExitLines = EffectiveExitLines();
        products = products.Where(product =>
            (dbContext.InventoryBalances
                    .Where(balance => balance.ProductId == product.Id)
                    .Sum(balance => (decimal?)balance.Quantity) ?? 0m) > 0m &&
            (allExitLines
                    .Where(line => line.ProductId == product.Id)
                    .Max(line => (DateTimeOffset?)line.Movement.OccurredAt) == null ||
             allExitLines
                    .Where(line => line.ProductId == product.Id)
                    .Max(line => (DateTimeOffset?)line.Movement.OccurredAt) < staleBeforeUtc));
        return new(products, warehouseDate, timeZone);
    }

    private IQueryable<Product> OrderStagnant(IQueryable<Product> products)
    {
        var allExitLines = EffectiveExitLines();
        return products
            .OrderBy(product => allExitLines
                .Where(line => line.ProductId == product.Id)
                .Max(line => (DateTimeOffset?)line.Movement.OccurredAt) != null)
            .ThenBy(product => allExitLines
                .Where(line => line.ProductId == product.Id)
                .Max(line => (DateTimeOffset?)line.Movement.OccurredAt))
            .ThenBy(product => product.Sku);
    }

    private IQueryable<StagnantProjection> ProjectStagnant(IQueryable<Product> products)
    {
        var allExitLines = EffectiveExitLines();
        return products.Select(product => new StagnantProjection(
            product.Id,
            product.Sku,
            product.Description,
            product.BaseUnitId,
            product.BaseUnit.Code,
            dbContext.InventoryBalances
                .Where(balance => balance.ProductId == product.Id)
                .Sum(balance => (decimal?)balance.Quantity) ?? 0m,
            allExitLines
                .Where(line => line.ProductId == product.Id)
                .Max(line => (DateTimeOffset?)line.Movement.OccurredAt),
            product.IsActive));
    }

    private IQueryable<Product> ApplyProductFilter(InventoryAnalyticsFilter filter)
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
                (product.ExternalReference != null && product.ExternalReference.ToUpper().Contains(term)) ||
                product.Barcodes.Any(barcode => barcode.Barcode.ToUpper().Contains(term)));
        }
        return query;
    }

    private IQueryable<InventoryMovementLine> EffectiveExitLines() =>
        dbContext.InventoryMovements
            .AsNoTracking()
            .WhereEffective(dbContext)
            .Where(movement => movement.Type == InventoryMovementType.Exit)
            .SelectMany(movement => movement.Lines)
            .AsQueryable();

    private static InventoryAnalyticsFilter Normalize(InventoryAnalyticsFilter filter) => filter with
    {
        ProductStatus = filter.ProductStatus is "inactive" or "all" ? filter.ProductStatus : "active",
        Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(),
        PageNumber = Math.Max(1, filter.PageNumber),
        PageSize = Math.Clamp(filter.PageSize, 1, 100)
    };

    private static int NormalizePage(int requestedPage, int pageSize, int totalCount) =>
        Math.Clamp(requestedPage, 1, Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize)));

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

    private static StagnantProductDto ToStagnantDto(
        StagnantProjection row,
        DateOnly warehouseDate,
        TimeZoneInfo timeZone)
    {
        var days = row.LastExitDateUtc is null
            ? (int?)null
            : warehouseDate.DayNumber - DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(row.LastExitDateUtc.Value, timeZone).DateTime).DayNumber;
        return new(
            row.ProductId,
            row.Sku,
            row.Description,
            row.UnitId,
            row.UnitCode,
            row.CurrentStock,
            row.LastExitDateUtc,
            days,
            Category(days)!.Value,
            row.IsActive);
    }

    private static DateTimeOffset ToUtcStart(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(30);
        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private enum OccupancyKind { Inactive, Blocked, Negative, Occupied, Empty }
    private sealed record OccupancyLocation(Guid Id, string RowCode, bool IsActive, bool IsBlocked);
    private sealed record OccupancyBalance(Guid LocationId, decimal Quantity);
    private sealed record OccupancyState(string RowCode, OccupancyKind State);
    private sealed record StagnantProjection(
        Guid ProductId,
        string Sku,
        string? Description,
        short UnitId,
        string UnitCode,
        decimal CurrentStock,
        DateTimeOffset? LastExitDateUtc,
        bool IsActive);
    private sealed record StagnantQueryContext(
        IQueryable<Product> Products,
        DateOnly WarehouseDate,
        TimeZoneInfo TimeZone);
}
