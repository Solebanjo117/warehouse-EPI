using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionDailyProductSummary(Guid ProductId, string Sku, decimal Planned,
    ProductionDailyAreaBalance Cutting, ProductionDailyAreaBalance Sewing, ProductionDailyAreaBalance ReadyToPack,
    IReadOnlyList<ProductionWeeklyIntention> Intentions)
{
    // Matches the worksheet's Status %: finished at Ready to Pack / (new plan + pending at start).
    public decimal? StatusRatio => !ReadyToPack.Applies ? null :
        Planned + ReadyToPack.Opening <= 0 ? 0 : ReadyToPack.Completed / (Planned + ReadyToPack.Opening);
}
public sealed record ProductionDailySummary(Guid WeekId, DateOnly WeekStart, DateOnly WeekEnd, DateOnly Through,
    ProductionScheduleWeekStatus Status, IReadOnlyList<ProductionDailyProductSummary> Products, bool ExplicitCarryover = false);

public sealed partial class ProductionDailyBalanceService
{
    public Task<ProductionDailySummary?> GetDailySummaryAsync(Guid weekId, ProductionWeeklyFilter filter, CancellationToken token = default) => GetDailySummaryAsync(weekId, filter, null, token);

    internal async Task<ProductionDailySummary?> GetDailySummaryAsync(Guid weekId, ProductionWeeklyFilter filter,
        ProductionBalanceScenario? scenario, CancellationToken token)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weekId, token);
        if (week is null) return null;
        var configuration = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var date = filter.Through < week.WeekStart ? week.WeekStart :
            filter.Through > week.WeekStart.AddDays(ProductionWeekCalendar.LastDayOffset) ? week.WeekStart.AddDays(ProductionWeekCalendar.LastDayOffset) : filter.Through;
        // Use the whole week's population, including products only planned or produced on another day.
        var weekly = (await GetWeeklyAsync(weekId, filter with { Through = week.WeekStart.AddDays(ProductionWeekCalendar.LastDayOffset) }, scenario, token))!;
        // Re-run the same order flow at the T1 cutoff; T2 receipts must not inflate the intermediate balance.
        var afterShift1 = (await GetAsync(weekId, false, false, token, date, configuration.Shift1Id, scenario))!
            .Rows.Where(x => x.Date == date).ToDictionary(x => x.ProductId);
        var openings = await db.ProductionScheduleLines.AsNoTracking()
            .Where(x => x.WeekId == weekId && !x.IsCancelled && !x.IsExtra && x.IsCarryover)
            .GroupBy(x => new { x.ProductId, x.PlannedDate, Area = x.StartArea ?? ProductionDailyArea.Cutting })
            .Select(x => new { x.Key.ProductId, x.Key.PlannedDate, x.Key.Area, Quantity = x.Sum(l => l.Quantity) })
            .ToListAsync(token);
        var products = weekly.Products.Select(product =>
        {
            var day = product.Days.Single(x => x.Date == date);
            var previous = product.Days.SingleOrDefault(x => x.Date == date.AddDays(-1));
            ProductionDailyAreaBalance Select(ProductionDailyBalanceRow row, ProductionDailyArea area) => area switch
            {
                ProductionDailyArea.Cutting => row.Cutting,
                ProductionDailyArea.Sewing => row.Sewing,
                _ => row.ReadyToPack
            };
            ProductionDailyAreaBalance Area(ProductionDailyArea area)
            {
                var current = Select(day.Balance, area);
                var before = previous is null ? null : Select(previous.Balance, area);
                var intermediate = afterShift1.TryGetValue(product.ProductId, out var shiftRow) ? Select(shiftRow, area) : null;
                var openingRows = openings.Where(x => x.ProductId == product.ProductId && x.Area == area).ToArray();
                // Weekly Opening includes all imported openings; schedule each exactly on its own date.
                var initial = before is null ? current.Opening - openingRows.Sum(x => x.Quantity)
                    : before.NetPending ?? before.Pending - before.ToReconcile;
                initial += openingRows.Where(x => x.PlannedDate == date).Sum(x => x.Quantity);
                return current with
                {
                    Opening = initial,
                    Completed = day.Shifts.Where(x => x.Area == area).Sum(x => x.Quantity),
                    CompletedShift1 = day.Shifts.Where(x => x.Area == area && x.ShiftId == configuration.Shift1Id).Sum(x => x.Quantity),
                    CompletedShift2 = day.Shifts.Where(x => x.Area == area && x.ShiftId == configuration.Shift2Id).Sum(x => x.Quantity),
                    PendingAfterShift1 = intermediate?.NetPending ?? intermediate?.Pending ?? 0,
                    Extra = week.ExplicitCarryover ? current.Extra : current.Extra - (before?.Extra ?? 0)
                };
            }
            return new ProductionDailyProductSummary(product.ProductId, product.Sku, day.Planned,
                Area(ProductionDailyArea.Cutting), Area(ProductionDailyArea.Sewing), Area(ProductionDailyArea.ReadyToPack),
                product.Intentions.Where(x => x.Date == date).ToArray());
        }).ToArray();
        return new(weekId, week.WeekStart, week.WeekEnd, date, week.Status, products, week.ExplicitCarryover);
    }
}
