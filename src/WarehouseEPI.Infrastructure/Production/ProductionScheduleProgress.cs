using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionScheduleDayCoverage(decimal Required, decimal Covered)
{
    public decimal Pending => Required - Covered;
    public decimal? Ratio => Required > 0 ? Covered / Required : null;
    public decimal? Percent => Ratio is decimal ratio
        ? decimal.Round(ratio * 100, 1, MidpointRounding.AwayFromZero) : null;

    public static ProductionScheduleDayCoverage Create(decimal cumulativeRequirement,
        decimal previousRequirement, decimal weekProduced, decimal initialCredit)
    {
        var required = Math.Max(0, cumulativeRequirement - previousRequirement);
        var available = Math.Max(0, weekProduced + initialCredit - previousRequirement);
        return new(required, Math.Min(required, available));
    }
}

public sealed record ProductionScheduleAreaProgress(ProductionDailyArea Area, bool Applies,
    ProductionAreaCoverage Daily, decimal CumulativeRequirement, decimal CumulativeProduced,
    decimal InitialCredit)
{
    public ProductionScheduleDayCoverage Coverage { get; init; } = new(0, 0);
    public decimal Target => Daily.Target;
    public decimal Produced => Daily.Produced;
    public decimal Balance => Daily.Balance;
    public bool CoveredByAdvance => Daily.CoveredByAdvance;
    public decimal? Percent => Daily.Percent;
    public decimal? Ratio => Daily.Ratio;
    public decimal AdvanceApplied => Daily.AdvanceApplied;
    public decimal BeforeAdvance => Daily.BeforeAdvance;
}

public sealed record ProductionScheduleDayProgress(int Day, decimal Planned,
    IReadOnlyList<ProductionScheduleAreaProgress> Areas);
public sealed record ProductionScheduleProductProgress(Guid ProductId, string Sku, string Unit,
    IReadOnlyList<ProductionScheduleDayProgress> Days)
{
    public IReadOnlyList<ProductionScheduleOpeningSummary> Openings { get; init; } = [];
}
public sealed record ProductionScheduleOpeningSummary(ProductionDailyArea Area, decimal Quantity);

public sealed partial class ProductionDailyBalanceService
{
    // The reporting balance already includes openings, applicable routes and effective captures.
    // Adding today's production back to its signed closing balance recovers today's target,
    // including an advance larger than today's plan, without changing any schedule quantity.
    public async Task<IReadOnlyList<ProductionScheduleProductProgress>> GetScheduleProgressAsync(
        Guid weekId, CancellationToken token = default)
    {
        var balance = await GetAsync(weekId, token);
        if (balance is null) return [];
        var ids = balance.Rows.Select(x => x.ProductId).Distinct().ToArray();
        var openings = balance.Rows.Where(x => x.Date == balance.WeekStart).SelectMany(row =>
            new[] { row.Cutting, row.Sewing, row.ReadyToPack }.Select(area => new { row.ProductId, area.Area,
                Quantity = area.SignedPending + area.Completed - area.ProgrammedToday })).Where(x => x.Quantity != 0).ToArray();
        var units = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, Unit = x.BaseUnit.Code })
            .ToDictionaryAsync(x => x.Id, x => x.Unit, token);
        return balance.Rows.GroupBy(x => x.ProductId).Select(product =>
        {
            var previous = new decimal[3];
            var cumulativeRequired = new decimal[3];
            var initialCredit = new decimal[3];
            var initialDebt = new decimal[3];
            var days = product.OrderBy(x => x.Date).Select(day =>
            {
                var areas = new[] { day.Cutting, day.Sewing, day.ReadyToPack }.Select(area =>
                {
                    var index = (int)area.Area;
                    var produced = area.Completed - previous[index];
                    previous[index] = area.Completed;
                    var signedBefore = area.SignedPending + produced - area.OpeningToday - area.ProgrammedToday;
                    if (day.Date == balance.WeekStart)
                    {
                        initialCredit[index] = Math.Max(0, -signedBefore);
                        initialDebt[index] = Math.Max(0, signedBefore);
                    }
                    cumulativeRequired[index] += area.ProgrammedToday + area.OpeningToday;
                    var daily = ProductionAreaCoverage.Create(area.Applies, signedBefore, area.OpeningToday,
                        area.ProgrammedToday, produced, area.SignedPending);
                    return new ProductionScheduleAreaProgress(area.Area, area.Applies, daily,
                        initialDebt[index] + cumulativeRequired[index], area.Completed, initialCredit[index]);
                }).ToArray();
                return new ProductionScheduleDayProgress(day.Date.DayNumber - balance.WeekStart.DayNumber,
                    day.NewPlan, areas);
            }).ToArray();
            var weekProduced = days[^1].Areas.Select(x => x.CumulativeProduced).ToArray();
            var previousRequirement = new decimal[3];
            // Allocate the saved week's effective production to the oldest due target first.
            // Daily production and signed balances above remain tied to their actual dates.
            var recoveredDays = days.Select(day => day with
            {
                Areas = day.Areas.Select(area =>
                {
                    var index = (int)area.Area;
                    var coverage = ProductionScheduleDayCoverage.Create(area.CumulativeRequirement,
                        previousRequirement[index], weekProduced[index], area.InitialCredit);
                    previousRequirement[index] = area.CumulativeRequirement;
                    return area with { Coverage = area.Applies ? coverage : new(0, 0) };
                }).ToArray()
            }).ToArray();
            return new ProductionScheduleProductProgress(product.Key, product.First().Sku, units[product.Key], recoveredDays)
            { Openings = openings.Where(x => x.ProductId == product.Key).OrderBy(x => x.Area)
                .Select(x => new ProductionScheduleOpeningSummary(x.Area, x.Quantity)).ToArray() };
        }).OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ProductId).ToArray();
    }
}
