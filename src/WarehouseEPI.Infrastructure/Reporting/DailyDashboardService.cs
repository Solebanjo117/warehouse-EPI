using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>Construye las métricas ligeras del tablero operativo diario.</summary>
public sealed class DailyDashboardService(
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService)
{
    private const int DefaultTrendDays = 14;
    private const int MaximumTrendDays = 31;

    public async Task<DailyDashboardSnapshotDto> GetSnapshotAsync(
        DateTimeOffset nowUtc,
        int trendDays = DefaultTrendDays,
        CancellationToken cancellationToken = default)
    {
        var normalizedTrendDays = Math.Clamp(trendDays, 1, MaximumTrendDays);
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var generatedAtLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var warehouseDate = DateOnly.FromDateTime(generatedAtLocal.DateTime);
        var firstDate = warehouseDate.AddDays(-(normalizedTrendDays - 1));
        var fromUtc = ToUtcStart(firstDate, timeZone);
        var toUtc = ToUtcStart(warehouseDate.AddDays(1), timeZone);

        var activityRows = await dbContext.InventoryMovements
            .AsNoTracking()
            .WhereEffective(dbContext)
            .Where(movement => movement.OccurredAt >= fromUtc && movement.OccurredAt < toUtc)
            .Select(movement => new ActivityProjection(
                movement.Id,
                movement.OccurredAt,
                movement.Type))
            .ToListAsync(cancellationToken);

        var movementIds = activityRows.Select(row => row.MovementId).ToArray();
        var productRows = movementIds.Length == 0
            ? new List<MovementProductProjection>()
            : await dbContext.InventoryMovementLines
                .AsNoTracking()
                .Where(line => movementIds.Contains(line.MovementId))
                .Select(line => new MovementProductProjection(line.MovementId, line.ProductId, line.Product.Sku, line.Product.Description))
                .ToListAsync(cancellationToken);
        var productsByMovement = productRows.ToLookup(row => row.MovementId);

        var localRows = activityRows
            .SelectMany(row => productsByMovement[row.MovementId]
                .DistinctBy(product => product.ProductId)
                .Select(product => new LocalActivityProjection(
                    row.MovementId,
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.OccurredAt, timeZone).DateTime),
                    row.MovementType,
                    product.ProductId,
                    product.Sku,
                    product.Description)))
            .ToArray();

        var locationRows = movementIds.Length == 0 ? [] : await dbContext.InventoryBalanceChanges.AsNoTracking()
            .Where(change => movementIds.Contains(change.MovementLine.MovementId))
            .Select(change => new LocationActivityProjection(change.MovementLine.MovementId,
                change.MovementLine.Movement.OccurredAt, change.Location.Code, change.Location.Description, change.Location.RowCode))
            .Distinct().ToListAsync(cancellationToken);
        var localLocations = locationRows.Select(row => row with
        {
            Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.OccurredAt, timeZone).DateTime)
        }).ToArray();

        var points = Enumerable.Range(0, normalizedTrendDays)
            .Select(offset => CreateActivityPoint(firstDate.AddDays(offset), localRows))
            .ToArray();

        var negativePositionsCount = await dbContext.InventoryBalances
            .AsNoTracking()
            .GroupBy(balance => new { balance.ProductId, balance.LocationId })
            .Select(balances => balances.Sum(balance => balance.Quantity))
            .CountAsync(quantity => quantity < 0m, cancellationToken);

        var lowStockProductsCount = await (
            from product in dbContext.Products.AsNoTracking()
            where product.IsActive
            join balance in dbContext.InventoryBalances.AsNoTracking()
                on product.Id equals balance.ProductId into balances
            let total = balances.Sum(balance => (decimal?)balance.Quantity) ?? 0m
            where total < product.MinimumStock
            select product.Id)
            .CountAsync(cancellationToken);

        var today = points[^1];
        var comparison = CreateComparison(warehouseDate, localRows, localLocations);
        return new(
            warehouseDate,
            generatedAtLocal,
            new DailyDashboardMetricsDto(
                today.TotalEffectiveOperations,
                negativePositionsCount,
                lowStockProductsCount,
                today.AdjustmentCount,
                points),
            comparison);
    }

    private static MovementActivityPointDto CreateActivityPoint(
        DateOnly date,
        IReadOnlyCollection<LocalActivityProjection> rows)
    {
        var dailyRows = rows.Where(row => row.Date == date).ToArray();
        return new(
            date,
            date.ToString("ddd dd/MM", CultureInfo.GetCultureInfo("es-MX")),
            CountMovements(dailyRows, InventoryMovementType.Entry),
            CountMovements(dailyRows, InventoryMovementType.Exit),
            CountMovements(dailyRows, InventoryMovementType.Transfer),
            CountMovements(dailyRows, InventoryMovementType.Adjustment),
            dailyRows.Select(row => row.MovementId).Distinct().Count(),
            dailyRows.Select(row => row.ProductId).Distinct().Count());
    }

    private static int CountMovements(
        IEnumerable<LocalActivityProjection> rows,
        InventoryMovementType type) =>
        rows.Where(row => row.MovementType == type)
            .Select(row => row.MovementId)
            .Distinct()
            .Count();

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

    private sealed record ActivityProjection(
        Guid MovementId,
        DateTimeOffset OccurredAt,
        InventoryMovementType MovementType);

    private static OperationalComparisonDto CreateComparison(DateOnly today,
        IReadOnlyCollection<LocalActivityProjection> rows,
        IReadOnlyCollection<LocationActivityProjection> locations)
    {
        var currentStart = today.AddDays(-6);
        var previousStart = today.AddDays(-13);
        var todayRows = rows.Where(x => x.Date == today).ToArray();
        var yesterdayRows = rows.Where(x => x.Date == today.AddDays(-1)).ToArray();
        var current = rows.Where(x => x.Date >= currentStart && x.Date <= today).ToArray();
        var previous = rows.Where(x => x.Date >= previousStart && x.Date < currentStart).ToArray();
        var currentLocations = locations.Where(x => x.Date >= currentStart && x.Date <= today).ToArray();
        var previousLocations = locations.Where(x => x.Date >= previousStart && x.Date < currentStart).ToArray();
        return new(
            Compare(todayRows.Select(x => x.MovementId).Distinct().Count(), yesterdayRows.Select(x => x.MovementId).Distinct().Count()),
            Compare(todayRows.Where(x => x.MovementType == InventoryMovementType.Adjustment).Select(x => x.MovementId).Distinct().Count(), yesterdayRows.Where(x => x.MovementType == InventoryMovementType.Adjustment).Select(x => x.MovementId).Distinct().Count()),
            Compare(current.Select(x => x.MovementId).Distinct().Count(), previous.Select(x => x.MovementId).Distinct().Count()),
            Compare(current.Where(x => x.MovementType == InventoryMovementType.Adjustment).Select(x => x.MovementId).Distinct().Count(), previous.Where(x => x.MovementType == InventoryMovementType.Adjustment).Select(x => x.MovementId).Distinct().Count()),
            Compare(current.Select(x => x.ProductId).Distinct().Count(), previous.Select(x => x.ProductId).Distinct().Count()),
            Drivers(current.GroupBy(x => new { x.Sku, x.Description }).Select(g => (g.Key.Sku, g.Key.Description, g.Select(x => x.MovementId).Distinct().Count())), previous.GroupBy(x => x.Sku).ToDictionary(g => g.Key, g => g.Select(x => x.MovementId).Distinct().Count()), "productCode"),
            Drivers(currentLocations.Where(x => x.RowCode != null).GroupBy(x => new { Code = x.RowCode!, Description = (string?)null }).Select(g => (g.Key.Code, g.Key.Description, g.Select(x => x.MovementId).Distinct().Count())), previousLocations.Where(x => x.RowCode != null).GroupBy(x => x.RowCode!).ToDictionary(g => g.Key, g => g.Select(x => x.MovementId).Distinct().Count()), "locationCode"),
            Drivers(currentLocations.GroupBy(x => new { x.Code, x.Description }).Select(g => (g.Key.Code, g.Key.Description, g.Select(x => x.MovementId).Distinct().Count())), previousLocations.GroupBy(x => x.Code).ToDictionary(g => g.Key, g => g.Select(x => x.MovementId).Distinct().Count()), "locationCode"));
    }

    private static MetricComparisonDto Compare(int current, int previous)
    {
        var delta = current - previous;
        var state = current == 0 && previous == 0 ? MetricComparisonState.NoActivity : previous == 0 ? MetricComparisonState.New : delta > 0 ? MetricComparisonState.Increased : delta < 0 ? MetricComparisonState.Decreased : MetricComparisonState.Unchanged;
        return new(current, previous, delta, previous == 0 ? null : Math.Round(delta * 100m / previous, 1), state);
    }

    private static IReadOnlyList<OperationalDriverDto> Drivers(IEnumerable<(string Code, string? Description, int Current)> current,
        IReadOnlyDictionary<string, int> previous, string routeParameter)
    {
        var currentByCode = current.ToDictionary(x => x.Code, StringComparer.Ordinal);
        return currentByCode.Keys.Union(previous.Keys, StringComparer.Ordinal)
            .Select(code =>
            {
                currentByCode.TryGetValue(code, out var currentValue);
                var currentCount = currentValue.Current;
                var previousCount = previous.GetValueOrDefault(code);
                return new OperationalDriverDto(code, currentValue.Description, currentCount, previousCount,
                    currentCount - previousCount, $"/Inventory?{routeParameter}={Uri.EscapeDataString(code)}");
            })
            .OrderByDescending(x => x.Delta).ThenByDescending(x => x.Current).ThenBy(x => x.Code, StringComparer.Ordinal)
            .Take(5).ToArray();
    }

    private sealed record MovementProductProjection(Guid MovementId, Guid ProductId, string Sku, string? Description);

    private sealed record LocalActivityProjection(
        Guid MovementId,
        DateOnly Date,
        InventoryMovementType MovementType,
        Guid ProductId,
        string Sku,
        string? Description);

    private sealed record LocationActivityProjection(Guid MovementId, DateTimeOffset OccurredAt, string Code,
        string? Description, string? RowCode, DateOnly Date = default);
}
