using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionWeekCloseArea(bool Applies, decimal Opening, decimal Shift1,
    decimal PendingAfterShift1, decimal Shift2, decimal Pending, decimal ToReconcile,
    decimal Completed)
{
    public decimal? CompletionRatio(decimal planned) => Applies && planned > 0 ? Completed / planned : null;
}

public sealed record ProductionWeekCloseProduct(Guid ProductId, string Sku, string Unit,
    decimal SundayPlan, decimal WeeklyPlan, ProductionWeekCloseArea Cutting,
    ProductionWeekCloseArea Sewing, ProductionWeekCloseArea ReadyToPack)
{
    public IReadOnlyList<ProductionWeekCloseArea> Areas => [Cutting, Sewing, ReadyToPack];
    public bool HasSundayActivity => SundayPlan != 0 || Areas.Any(x => x.Applies &&
        (x.Opening != 0 || x.Shift1 != 0 || x.Shift2 != 0 || x.Pending != 0 || x.ToReconcile != 0));
}

public sealed record ProductionWeekCloseTotal(string Unit, decimal SundayPlan, decimal WeeklyPlan,
    ProductionWeekCloseArea Cutting, ProductionWeekCloseArea Sewing, ProductionWeekCloseArea ReadyToPack)
{
    public IReadOnlyList<ProductionWeekCloseArea> Areas => [Cutting, Sewing, ReadyToPack];
}

public sealed record ProductionWeekShiftComparisonArea(ProductionDailyArea Area, decimal Shift1, decimal Shift2)
{
    public decimal Total => Shift1 + Shift2;
    public decimal? Shift1Share => Total == 0 ? null : Shift1 / Total;
    public decimal? Shift2Share => Total == 0 ? null : Shift2 / Total;
}

public sealed record ProductionWeekShiftComparison(string Unit, IReadOnlyList<ProductionWeekShiftComparisonArea> Areas)
{
    public decimal Shift1 => Areas.Sum(x => x.Shift1);
    public decimal Shift2 => Areas.Sum(x => x.Shift2);
    public decimal Total => Shift1 + Shift2;
    public decimal? Shift1Share => Total == 0 ? null : Shift1 / Total;
    public decimal? Shift2Share => Total == 0 ? null : Shift2 / Total;
}

public sealed record ProductionWeekClose(DateOnly WeekStart, DateOnly WeekEnd,
    ProductionScheduleWeekStatus Status, IReadOnlyList<ProductionWeekCloseProduct> Products,
    IReadOnlyList<ProductionWeekCloseTotal> Totals)
{
    public IReadOnlyList<ProductionWeekShiftComparison> ShiftComparison { get; init; } = [];
    public IReadOnlyList<ProductionWeekCloseProduct> PendingProducts => Products.Where(x => x.HasSundayActivity).ToArray();
    public ProductionWeekSummaryTotal SummaryTotal => new(Products.Sum(x => x.WeeklyPlan),
        Products.Where(x => x.Cutting.Applies).Sum(x => x.Cutting.Completed),
        Products.Where(x => x.Sewing.Applies).Sum(x => x.Sewing.Completed),
        Products.Where(x => x.ReadyToPack.Applies).Sum(x => x.ReadyToPack.Completed));
}

public sealed record ProductionWeekSummaryTotal(decimal WeeklyPlan, decimal Cutting,
    decimal Sewing, decimal ReadyToPack)
{
    public IReadOnlyList<decimal> Completed => [Cutting, Sewing, ReadyToPack];
}

public sealed partial class ProductionDailyBalanceService
{
    public Task<ProductionWeekClose?> GetWeekCloseAsync(Guid weekId, ProductionWeeklyFilter filter,
        CancellationToken token = default) => GetWeekCloseAsync(weekId, filter, null, token);

    internal async Task<ProductionWeekClose?> GetWeekCloseAsync(Guid weekId, ProductionWeeklyFilter filter,
        ProductionBalanceScenario? scenario, CancellationToken token)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking()
            .Where(x => x.Id == weekId).Select(x => new { x.WeekStart, x.WeekEnd }).SingleOrDefaultAsync(token);
        if (week is null) return null;
        var closeFilter = filter with { Through = week.WeekEnd };
        var weekly = await GetWeeklyAsync(weekId, closeFilter, scenario, token);
        var sunday = await GetDailySummaryAsync(weekId, closeFilter, scenario, token);
        if (weekly is null || sunday is null) return null;
        var ids = weekly.Products.Select(x => x.ProductId).ToArray();
        var units = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, Unit = x.BaseUnit.Code })
            .ToDictionaryAsync(x => x.Id, x => x.Unit, token);
        var dailyByProduct = sunday.Products.ToDictionary(x => x.ProductId);
        static ProductionWeekCloseArea Area(ProductionDailyAreaBalance day, ProductionDailyAreaBalance week) =>
            new(week.Applies, day.Opening, day.CompletedShift1, day.PendingAfterShift1,
                day.CompletedShift2, day.NetPending ?? day.Pending, day.ToReconcile, week.Completed);
        var rows = weekly.Products.Select(product =>
        {
            var day = dailyByProduct[product.ProductId];
            return new ProductionWeekCloseProduct(product.ProductId, product.Sku,
                units.GetValueOrDefault(product.ProductId) ?? "—", day.Planned, product.Planned,
                Area(day.Cutting, product.Cutting), Area(day.Sewing, product.Sewing),
                Area(day.ReadyToPack, product.ReadyToPack));
        }).ToArray();
        static ProductionWeekCloseArea Sum(IEnumerable<ProductionWeekCloseArea> areas)
        {
            var applicable = areas.Where(x => x.Applies).ToArray();
            return new(applicable.Length > 0, applicable.Sum(x => x.Opening),
                applicable.Sum(x => x.Shift1), applicable.Sum(x => x.PendingAfterShift1),
                applicable.Sum(x => x.Shift2), applicable.Sum(x => x.Pending),
                applicable.Sum(x => x.ToReconcile), applicable.Sum(x => x.Completed));
        }
        var totals = rows.GroupBy(x => x.Unit, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProductionWeekCloseTotal(group.Key, group.Sum(x => x.SundayPlan),
                group.Sum(x => x.WeeklyPlan), Sum(group.Select(x => x.Cutting)),
                Sum(group.Select(x => x.Sewing)), Sum(group.Select(x => x.ReadyToPack))))
            .ToArray();
        var configuration = await db.ProductionDailyConfigurations.AsNoTracking()
            .Where(x => x.Id == 1).Select(x => new { x.Shift1Id, x.Shift2Id }).SingleAsync(token);
        var comparison = totals.Select(total =>
        {
            var productsInUnit = weekly.Products.Where(x =>
                string.Equals(units.GetValueOrDefault(x.ProductId), total.Unit, StringComparison.OrdinalIgnoreCase));
            var shifts = productsInUnit.SelectMany(x => x.Days).SelectMany(x => x.Shifts).ToArray();
            var areas = Enum.GetValues<ProductionDailyArea>()
                .Where(area => filter.Area is null || filter.Area == area)
                .Select(area => new ProductionWeekShiftComparisonArea(area,
                    shifts.Where(x => x.Area == area && x.ShiftId == configuration.Shift1Id).Sum(x => x.Quantity),
                    shifts.Where(x => x.Area == area && x.ShiftId == configuration.Shift2Id).Sum(x => x.Quantity)))
                .ToArray();
            return new ProductionWeekShiftComparison(total.Unit, areas);
        }).ToArray();
        return new ProductionWeekClose(week.WeekStart, week.WeekEnd, weekly.Status, rows, totals)
        {
            ShiftComparison = comparison
        };
    }
}
