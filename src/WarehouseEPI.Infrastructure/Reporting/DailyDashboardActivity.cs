using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed record DashboardActivityDto(DateOnly WarehouseDate, DateTimeOffset GeneratedAtLocal,
    int Days, IReadOnlyList<MovementActivityPointDto> Points);
public sealed record DashboardLocationDto(string Code, int Operations);
public sealed record DashboardProductDto(Guid ProductId, string Sku, string? Description,
    int Operations, decimal Percent, int EntryCount, int ExitCount, int TransferCount, int AdjustmentCount,
    IReadOnlyList<DashboardLocationDto> Locations);
public sealed record DashboardProductsDto(DateOnly From, DateOnly To, DateTimeOffset GeneratedAtLocal,
    int Days, int PageNumber, int TotalProducts, int TotalOperations, IReadOnlyList<DashboardProductDto> Items)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalProducts / 10d));
}

public sealed partial class DailyDashboardService
{
    public async Task<DashboardActivityDto> GetActivityAsync(DateTimeOffset nowUtc, int days,
        CancellationToken cancellationToken = default)
    {
        var period = await ActivityPeriodAsync(nowUtc, days, cancellationToken);
        var movements = ActivityMovements(period);
        var counts = await movements.GroupBy(DayIndex<InventoryMovement>(m => m.OccurredAt, period))
            .Select(g => new
            {
                Day = g.Key,
                Total = g.Count(),
                Entry = g.Count(m => m.Type == InventoryMovementType.Entry),
                Exit = g.Count(m => m.Type == InventoryMovementType.Exit),
                Transfer = g.Count(m => m.Type == InventoryMovementType.Transfer),
                Adjustment = g.Count(m => m.Type == InventoryMovementType.Adjustment)
            })
            .ToDictionaryAsync(x => x.Day, cancellationToken);
        var skus = await dbContext.InventoryMovementLines.AsNoTracking()
            .Where(line => movements.Select(m => m.Id).Contains(line.MovementId))
            .GroupBy(DayIndex<InventoryMovementLine>(line => line.Movement.OccurredAt, period))
            .Select(g => new { Day = g.Key, Count = g.Select(line => line.ProductId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Day, cancellationToken);
        var points = Enumerable.Range(0, days).Select(index =>
        {
            counts.TryGetValue(index, out var count);
            var date = period.From.AddDays(index);
            return new MovementActivityPointDto(date, date.ToString("ddd dd/MM", CultureInfo.GetCultureInfo("es-MX")),
                count?.Entry ?? 0, count?.Exit ?? 0, count?.Transfer ?? 0, count?.Adjustment ?? 0,
                count?.Total ?? 0, skus.GetValueOrDefault(index)?.Count ?? 0);
        }).ToArray();
        return new(period.To, period.GeneratedAtLocal, days, points);
    }

    public async Task<DashboardProductsDto> GetActivityProductsAsync(DateTimeOffset nowUtc, int days, int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        var period = await ActivityPeriodAsync(nowUtc, days, cancellationToken);
        var movements = ActivityMovements(period);
        var totalOperations = await movements.CountAsync(cancellationToken);
        var lines = dbContext.InventoryMovementLines.AsNoTracking()
            .Where(line => movements.Select(m => m.Id).Contains(line.MovementId));
        var products = lines.Select(line => new
        {
            line.ProductId,
            line.Product.Sku,
            line.Product.Description,
            line.MovementId,
            line.Movement.Type
        }).Distinct()
            .GroupBy(line => new { line.ProductId, line.Sku, line.Description })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.Sku,
                g.Key.Description,
                Operations = g.Count(),
                Entry = g.Count(x => x.Type == InventoryMovementType.Entry),
                Exit = g.Count(x => x.Type == InventoryMovementType.Exit),
                Transfer = g.Count(x => x.Type == InventoryMovementType.Transfer),
                Adjustment = g.Count(x => x.Type == InventoryMovementType.Adjustment)
            });
        var totalProducts = await products.CountAsync(cancellationToken);
        pageNumber = Math.Clamp(pageNumber, 1, Math.Max(1, (int)Math.Ceiling(totalProducts / 10d)));
        var page = await products.OrderByDescending(x => x.Operations).ThenBy(x => x.Sku).ThenBy(x => x.ProductId)
            .Skip((pageNumber - 1) * 10).Take(10).ToListAsync(cancellationToken);
        var items = new List<DashboardProductDto>();
        foreach (var product in page)
        {
            var productLines = lines.Where(line => line.ProductId == product.ProductId);
            // Source/destination are recorded on the movement, including documentary WIP destinations.
            var locations = productLines.Where(line => line.SourceLocationId != null)
                .Select(line => new { line.MovementId, Code = line.SourceLocation!.Code })
                .Union(productLines.Where(line => line.DestinationLocationId != null)
                    .Select(line => new { line.MovementId, Code = line.DestinationLocation!.Code }))
                .Union(productLines.Where(line => line.Movement.OperationalAreaId != null)
                    .Select(line => new { line.MovementId, Code = line.Movement.OperationalArea!.Code }));
            var topLocations = await locations.GroupBy(x => x.Code)
                .Select(g => new { Code = g.Key, Operations = g.Count() })
                .OrderByDescending(x => x.Operations).ThenBy(x => x.Code).Take(5).ToListAsync(cancellationToken);
            items.Add(new(product.ProductId, product.Sku, product.Description, product.Operations,
                totalOperations == 0 ? 0 : Math.Round(product.Operations * 100m / totalOperations, 1),
                product.Entry, product.Exit, product.Transfer, product.Adjustment,
                topLocations.Select(x => new DashboardLocationDto(x.Code, x.Operations)).ToArray()));
        }
        return new(period.From, period.To, period.GeneratedAtLocal, days, pageNumber, totalProducts, totalOperations, items);
    }

    private sealed record ActivityPeriod(DateOnly From, DateOnly To, DateTimeOffset GeneratedAtLocal,
        DateTimeOffset[] Boundaries);

    private async Task<ActivityPeriod> ActivityPeriodAsync(DateTimeOffset nowUtc, int days, CancellationToken token)
    {
        if (days is not (7 or 14 or 90)) throw new ArgumentOutOfRangeException(nameof(days));
        var settings = await settingsService.GetAsync(token);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var from = today.AddDays(1 - days);
        return new(from, today, local, Enumerable.Range(0, days + 1).Select(i => ToUtcStart(from.AddDays(i), zone)).ToArray());
    }

    private IQueryable<InventoryMovement> ActivityMovements(ActivityPeriod period)
    {
        var from = period.Boundaries[0];
        var to = period.Boundaries[^1];
        return dbContext.InventoryMovements.AsNoTracking().WhereEffective(dbContext)
            .Where(m => m.OccurredAt >= from && m.OccurredAt < to);
    }

    // A balanced SQL CASE over local-midnight UTC boundaries keeps aggregation in the database,
    // handles DST, and works identically with PostgreSQL and the isolated InMemory tests.
    private static Expression<Func<T, int>> DayIndex<T>(Expression<Func<T, DateTimeOffset>> timestamp, ActivityPeriod period)
    {
        Expression Bucket(int start, int end)
        {
            if (start == end) return Expression.Constant(start);
            var middle = (start + end + 1) / 2;
            return Expression.Condition(Expression.LessThan(timestamp.Body, Expression.Constant(period.Boundaries[middle])),
                Bucket(start, middle - 1), Bucket(middle, end));
        }
        return Expression.Lambda<Func<T, int>>(Bucket(0, period.Boundaries.Length - 2), timestamp.Parameters);
    }
}
