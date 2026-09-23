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

    public async Task<LotAgingReportDto> GetLotAgingPageAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var warehouseDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);

        var (allLots, summary) = await QueryLotAgingDataAsync(normalized, warehouseDate, timeZone, cancellationToken);
        var filteredLots = FilterAndOrderLots(allLots, normalized);
        var totalCount = filteredLots.Count;
        var pageNumber = NormalizePage(normalized.PageNumber, normalized.PageSize, totalCount);
        var pageItems = filteredLots
            .Skip((pageNumber - 1) * normalized.PageSize)
            .Take(normalized.PageSize)
            .ToArray();

        var page = new InventoryAnalyticsPage<LotAgingItemDto>(pageItems, totalCount, pageNumber, normalized.PageSize);
        return new(summary, page);
    }

    public async Task<InventoryAnalyticsExportBatch<LotAgingItemDto>> GetLotAgingExportAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        int maximumRows = 10000,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var limit = Math.Clamp(maximumRows, 1, 50000);
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var warehouseDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);

        var (allLots, _) = await QueryLotAgingDataAsync(normalized, warehouseDate, timeZone, cancellationToken);
        var filteredLots = FilterAndOrderLots(allLots, normalized);

        if (filteredLots.Count > limit)
            return new([], filteredLots.Count, limit);

        return new(filteredLots, filteredLots.Count, limit);
    }

    public async Task<SkuCoverageReportDto> GetCoveragePageAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var (allSkus, summary) = await QueryCoverageDataAsync(normalized, nowUtc, cancellationToken);
        var filteredSkus = FilterAndOrderCoverage(allSkus, normalized);

        var totalCount = filteredSkus.Count;
        var pageNumber = NormalizePage(normalized.PageNumber, normalized.PageSize, totalCount);
        var pageItems = filteredSkus
            .Skip((pageNumber - 1) * normalized.PageSize)
            .Take(normalized.PageSize)
            .ToArray();

        var page = new InventoryAnalyticsPage<SkuCoverageItemDto>(pageItems, totalCount, pageNumber, normalized.PageSize);
        return new(summary, page);
    }

    public async Task<InventoryAnalyticsExportBatch<SkuCoverageItemDto>> GetCoverageExportAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        int maximumRows = 10000,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(filter);
        var limit = Math.Clamp(maximumRows, 1, 50000);
        var (allSkus, _) = await QueryCoverageDataAsync(normalized, nowUtc, cancellationToken);
        var filteredSkus = FilterAndOrderCoverage(allSkus, normalized);

        if (filteredSkus.Count > limit)
            return new([], filteredSkus.Count, limit);

        return new(filteredSkus, filteredSkus.Count, limit);
    }

    private async Task<(List<LotAgingItemDto> AllLots, LotAgingSummaryDto Summary)> QueryLotAgingDataAsync(
        InventoryAnalyticsFilter filter,
        DateOnly warehouseDate,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        var query = dbContext.InventoryBalances
            .AsNoTracking()
            .Where(b => b.Quantity > 0m && b.LotId != null);

        if (filter.ProductStatus == "inactive")
            query = query.Where(b => !b.Product.IsActive);
        else if (filter.ProductStatus != "all")
            query = query.Where(b => b.Product.IsActive);

        if (filter.UnitId is not null)
            query = query.Where(b => b.Product.BaseUnitId == filter.UnitId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            query = query.Where(b =>
                b.Product.Sku.ToUpper().Contains(term) ||
                (b.Product.Description != null && b.Product.Description.ToUpper().Contains(term)) ||
                (b.Product.ExternalReference != null && b.Product.ExternalReference.ToUpper().Contains(term)) ||
                b.Product.Barcodes.Any(barcode => barcode.Barcode.ToUpper().Contains(term)) ||
                (b.Lot != null && b.Lot.Number.ToUpper().Contains(term)));
        }

        var balanceRows = await query
            .Select(b => new
            {
                LotId = b.LotId!.Value,
                b.ProductId,
                b.Product.Sku,
                b.Product.Description,
                UnitId = b.Product.BaseUnitId,
                UnitCode = b.Product.BaseUnit.Code,
                LotNumber = b.Lot!.Number,
                LotDate = b.Lot.LotDate,
                LotCreatedAt = b.Lot.CreatedAt,
                LocationCode = b.Location.Code,
                b.Quantity
            })
            .ToListAsync(cancellationToken);

        var lots = balanceRows
            .GroupBy(b => b.LotId)
            .Select(g =>
            {
                var first = g.First();
                var effectiveDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(first.LotCreatedAt, timeZone).DateTime);
                var ageDays = Math.Max(0, warehouseDate.DayNumber - effectiveDate.DayNumber);
                var bucket = ClassifyLotAge(ageDays);
                var locations = g.GroupBy(x => x.LocationCode)
                    .Select(lg => new { LocationCode = lg.Key, Quantity = lg.Sum(x => x.Quantity) })
                    .OrderByDescending(x => x.Quantity)
                    .ThenBy(x => x.LocationCode, StringComparer.Ordinal)
                    .ToArray();

                return new LotAgingItemDto(
                    first.LotId,
                    first.ProductId,
                    first.Sku,
                    first.Description,
                    first.UnitId,
                    first.UnitCode,
                    first.LotNumber,
                    first.LotDate,
                    ageDays,
                    bucket,
                    FormatLotAgeBucket(bucket),
                    g.Sum(x => x.Quantity),
                    locations.Length,
                    locations.Length > 0 ? locations[0].LocationCode : string.Empty);
            })
            .ToList();

        var summary = new LotAgingSummaryDto(
            lots.Count,
            lots.Count(l => l.AgeBucket == LotAgeBucket.Days0To30),
            lots.Count(l => l.AgeBucket == LotAgeBucket.Days31To60),
            lots.Count(l => l.AgeBucket == LotAgeBucket.Days61To90),
            lots.Count(l => l.AgeBucket == LotAgeBucket.Days90Plus));

        return (lots, summary);
    }

    private static List<LotAgingItemDto> FilterAndOrderLots(
        List<LotAgingItemDto> allLots,
        InventoryAnalyticsFilter filter)
    {
        var items = allLots.AsEnumerable();
        if (filter.AgeBucket is not null && filter.AgeBucket.Value != LotAgeBucket.All)
        {
            items = items.Where(l => l.AgeBucket == filter.AgeBucket.Value);
        }

        return items
            .OrderByDescending(l => l.AgeDays)
            .ThenBy(l => l.Sku, StringComparer.Ordinal)
            .ThenBy(l => l.LotNumber, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<(List<SkuCoverageItemDto> AllSkus, SkuCoverageSummaryDto Summary)> QueryCoverageDataAsync(
        InventoryAnalyticsFilter filter,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var fromUtc = filter.FromUtc ?? nowUtc.AddDays(-30);
        var toUtc = filter.ToUtc ?? nowUtc;
        var windowDays = Math.Max(1, (int)Math.Round((toUtc - fromUtc).TotalDays));

        var productsQuery = ApplyProductFilter(filter);
        var products = await productsQuery
            .Select(p => new
            {
                p.Id,
                p.Sku,
                p.Description,
                p.BaseUnitId,
                UnitCode = p.BaseUnit.Code
            })
            .ToListAsync(cancellationToken);

        var productIds = products.Select(p => p.Id).ToArray();

        var availableStockByProduct = await dbContext.InventoryBalances
            .AsNoTracking()
            .Where(b => productIds.Contains(b.ProductId) &&
                        b.Location.Kind == LocationKind.Rack &&
                        b.Location.OperationalRole == LocationOperationalRole.Storage &&
                        b.Location.IsActive &&
                        !b.Location.IsBlocked)
            .GroupBy(b => b.ProductId)
            .Select(g => new { ProductId = g.Key, Stock = g.Sum(b => b.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Stock, cancellationToken);

        var effectiveQuery = dbContext.InventoryMovements
            .AsNoTracking()
            .WhereEffective(dbContext);

        var exitLines = await effectiveQuery
            .Where(m => (m.Type == InventoryMovementType.Exit || m.Type == InventoryMovementType.Transfer) &&
                        (m.Purpose == InventoryMovementPurpose.ProductionIssue ||
                         m.Purpose == InventoryMovementPurpose.GeneralExit) &&
                        m.OccurredAt >= fromUtc &&
                        m.OccurredAt < toUtc)
            .SelectMany(m => m.Lines)
            .Where(l => productIds.Contains(l.ProductId))
            .GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, Exits = g.Sum(l => l.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Exits, cancellationToken);

        var returnLines = await effectiveQuery
            .Where(m => (m.Type == InventoryMovementType.Entry || m.Type == InventoryMovementType.Transfer) &&
                        m.Purpose == InventoryMovementPurpose.WipWarehouseReturn &&
                        m.OccurredAt >= fromUtc &&
                        m.OccurredAt < toUtc)
            .SelectMany(m => m.Lines)
            .Where(l => productIds.Contains(l.ProductId))
            .GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, Returns = g.Sum(l => l.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Returns, cancellationToken);

        var lastExits = await effectiveQuery
            .Where(m => (m.Type == InventoryMovementType.Exit || m.Type == InventoryMovementType.Transfer) &&
                        (m.Purpose == InventoryMovementPurpose.ProductionIssue ||
                         m.Purpose == InventoryMovementPurpose.GeneralExit))
            .SelectMany(m => m.Lines)
            .Where(l => productIds.Contains(l.ProductId))
            .GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, LastExit = g.Max(l => (DateTimeOffset?)l.Movement.OccurredAt) })
            .ToDictionaryAsync(x => x.ProductId, x => x.LastExit, cancellationToken);

        var items = products.Select(p =>
        {
            var rawStock = availableStockByProduct.GetValueOrDefault(p.Id, 0m);
            var availableStock = Math.Max(0m, rawStock);
            var exits = exitLines.GetValueOrDefault(p.Id, 0m);
            var returns = returnLines.GetValueOrDefault(p.Id, 0m);
            var netConsumption = Math.Max(0m, exits - returns);
            var lastExitDate = lastExits.GetValueOrDefault(p.Id);

            decimal dailyAverage;
            decimal? coverageDays;
            CoverageClassification classification;

            if (netConsumption <= 0m)
            {
                dailyAverage = 0m;
                coverageDays = null;
                classification = CoverageClassification.NoRecentConsumption;
            }
            else if (availableStock <= 0m)
            {
                dailyAverage = Math.Round(netConsumption / windowDays, 4);
                coverageDays = 0m;
                classification = CoverageClassification.Exhausted;
            }
            else
            {
                var rawDailyAverage = netConsumption / windowDays;
                dailyAverage = Math.Round(rawDailyAverage, 4);
                var rawDays = availableStock / rawDailyAverage;
                coverageDays = Math.Round(rawDays, 1);

                if (rawDays < 7m)
                    classification = CoverageClassification.Critical;
                else if (rawDays <= 14m)
                    classification = CoverageClassification.Low;
                else if (rawDays <= 45m)
                    classification = CoverageClassification.Normal;
                else
                    classification = CoverageClassification.Excess;
            }

            return new SkuCoverageItemDto(
                p.Id,
                p.Sku,
                p.Description,
                p.BaseUnitId,
                p.UnitCode,
                availableStock,
                netConsumption,
                dailyAverage,
                coverageDays,
                classification,
                FormatCoverageClassification(classification),
                lastExitDate);
        }).ToList();

        var summary = new SkuCoverageSummaryDto(
            items.Count,
            items.Count(x => x.Classification == CoverageClassification.Critical),
            items.Count(x => x.Classification == CoverageClassification.Low),
            items.Count(x => x.Classification == CoverageClassification.Normal),
            items.Count(x => x.Classification == CoverageClassification.Excess),
            items.Count(x => x.Classification == CoverageClassification.NoRecentConsumption),
            items.Count(x => x.Classification == CoverageClassification.Exhausted));

        return (items, summary);
    }

    private static List<SkuCoverageItemDto> FilterAndOrderCoverage(
        List<SkuCoverageItemDto> allItems,
        InventoryAnalyticsFilter filter)
    {
        var query = allItems.AsEnumerable();
        if (filter.CoverageClassification is not null && filter.CoverageClassification.Value != CoverageClassification.All)
        {
            query = query.Where(x => x.Classification == filter.CoverageClassification.Value);
        }

        return query
            .OrderBy(x => CoveragePriority(x.Classification))
            .ThenBy(x => x.CoverageDays ?? decimal.MaxValue)
            .ThenBy(x => x.Sku, StringComparer.Ordinal)
            .ToList();
    }

    private static int CoveragePriority(CoverageClassification c) => c switch
    {
        CoverageClassification.Critical => 1,
        CoverageClassification.Exhausted => 2,
        CoverageClassification.Low => 3,
        CoverageClassification.Normal => 4,
        CoverageClassification.Excess => 5,
        CoverageClassification.NoRecentConsumption => 6,
        _ => 7
    };

    public static LotAgeBucket ClassifyLotAge(int ageDays) => ageDays switch
    {
        <= 30 => LotAgeBucket.Days0To30,
        <= 60 => LotAgeBucket.Days31To60,
        <= 90 => LotAgeBucket.Days61To90,
        _ => LotAgeBucket.Days90Plus
    };

    public static string FormatLotAgeBucket(LotAgeBucket bucket) => bucket switch
    {
        LotAgeBucket.Days0To30 => "0–30 días",
        LotAgeBucket.Days31To60 => "31–60 días",
        LotAgeBucket.Days61To90 => "61–90 días",
        LotAgeBucket.Days90Plus => "Más de 90 días",
        _ => "Todos"
    };

    public static string FormatCoverageClassification(CoverageClassification classification) => classification switch
    {
        CoverageClassification.Critical => "Crítico (< 7d)",
        CoverageClassification.Low => "Bajo (7–14d)",
        CoverageClassification.Normal => "Normal (15–45d)",
        CoverageClassification.Excess => "Exceso (> 45d)",
        CoverageClassification.NoRecentConsumption => "Sin consumo reciente",
        CoverageClassification.Exhausted => "Agotado",
        _ => "Todos"
    };

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
        if (filter.StagnantCategory is not null)
        {
            var sixtyDaysBeforeUtc = ToUtcStart(warehouseDate.AddDays(-59), timeZone);
            var ninetyDaysBeforeUtc = ToUtcStart(warehouseDate.AddDays(-89), timeZone);

            products = filter.StagnantCategory.Value switch
            {
                StagnantCategory.Days90Plus => products.Where(product =>
                    allExitLines.Where(line => line.ProductId == product.Id).Max(line => (DateTimeOffset?)line.Movement.OccurredAt) == null ||
                    allExitLines.Where(line => line.ProductId == product.Id).Max(line => (DateTimeOffset?)line.Movement.OccurredAt) < ninetyDaysBeforeUtc),

                StagnantCategory.Days60To89 => products.Where(product =>
                    allExitLines.Where(line => line.ProductId == product.Id).Max(line => (DateTimeOffset?)line.Movement.OccurredAt) != null &&
                    allExitLines.Where(line => line.ProductId == product.Id).Max(line => (DateTimeOffset?)line.Movement.OccurredAt) >= ninetyDaysBeforeUtc &&
                    allExitLines.Where(line => line.ProductId == product.Id).Max(line => (DateTimeOffset?)line.Movement.OccurredAt) < sixtyDaysBeforeUtc),

                StagnantCategory.Days30To59 => products.Where(product =>
                    allExitLines.Where(line => line.ProductId == product.Id).Max(line => (DateTimeOffset?)line.Movement.OccurredAt) != null &&
                    allExitLines.Where(line => line.ProductId == product.Id).Max(line => (DateTimeOffset?)line.Movement.OccurredAt) >= sixtyDaysBeforeUtc &&
                    allExitLines.Where(line => line.ProductId == product.Id).Max(line => (DateTimeOffset?)line.Movement.OccurredAt) < staleBeforeUtc),

                StagnantCategory.NeverExited => products.Where(product =>
                    allExitLines.Where(line => line.ProductId == product.Id).Max(line => (DateTimeOffset?)line.Movement.OccurredAt) == null),

                _ => products
            };
        }
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
