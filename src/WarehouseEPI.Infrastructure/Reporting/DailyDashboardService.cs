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
                .Select(line => new MovementProductProjection(line.MovementId, line.ProductId))
                .ToListAsync(cancellationToken);
        var productsByMovement = productRows.ToLookup(row => row.MovementId, row => row.ProductId);

        var localRows = activityRows
            .SelectMany(row => productsByMovement[row.MovementId]
                .Distinct()
                .Select(productId => new LocalActivityProjection(
                    row.MovementId,
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.OccurredAt, timeZone).DateTime),
                    row.MovementType,
                    productId)))
            .ToArray();

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
        return new(
            warehouseDate,
            generatedAtLocal,
            new DailyDashboardMetricsDto(
                today.TotalEffectiveOperations,
                negativePositionsCount,
                lowStockProductsCount,
                today.AdjustmentCount,
                points));
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

    private sealed record MovementProductProjection(Guid MovementId, Guid ProductId);

    private sealed record LocalActivityProjection(
        Guid MovementId,
        DateOnly Date,
        InventoryMovementType MovementType,
        Guid ProductId);
}
