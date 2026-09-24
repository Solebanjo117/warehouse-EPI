using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionWeeklyFilter(DateOnly Through, string? Sku = null, string? Reference = null,
    ProductionDailyArea? Area = null);
public sealed record ProductionWeeklyDay(DateOnly Date, decimal Planned,
    IReadOnlyList<ProductionWeeklyShift> Shifts, ProductionDailyBalanceRow Balance);
public sealed record ProductionWeeklyShift(ProductionDailyArea Area, Guid ShiftId, string Shift, decimal Quantity);
public sealed record ProductionWeeklyIntention(DateOnly Date, ProductionDailyArea Area, decimal Quantity);
public sealed record ProductionWeeklyProduct(Guid ProductId, string Sku, string? Description, decimal Planned,
    ProductionDailyAreaBalance Cutting, ProductionDailyAreaBalance Sewing, ProductionDailyAreaBalance ReadyToPack,
    IReadOnlyList<ProductionWeeklyDay> Days, IReadOnlyList<ProductionWeeklyIntention> Intentions);
public sealed record ProductionWeeklySummary(Guid WeekId, DateOnly WeekStart, DateOnly WeekEnd, DateOnly Through,
    ProductionScheduleWeekStatus Status, IReadOnlyList<ProductionWeeklyProduct> Products);

public sealed partial class ProductionDailyBalanceService
{
    public Task<ProductionWeeklySummary?> GetWeeklyAsync(Guid weekId, ProductionWeeklyFilter filter, CancellationToken token = default) => GetWeeklyAsync(weekId, filter, null, token);

    internal async Task<ProductionWeeklySummary?> GetWeeklyAsync(Guid weekId, ProductionWeeklyFilter filter,
        ProductionBalanceScenario? scenario, CancellationToken token)
    {
        var balance = await GetAsync(weekId, false, false, token, scenario: scenario);
        if (balance is null) return null;
        var end = balance.WeekStart.AddDays(ProductionWeekCalendar.LastDayOffset);
        var through = filter.Through < balance.WeekStart ? balance.WeekStart :
            filter.Through > end ? end : filter.Through;
        // SQL aggregates all active captures, independently of the paged history view.
        var excludedCaptures = scenario?.ReversedCaptures.ToArray() ?? [];
        var shifts = await db.ProductionDailyCaptures.AsNoTracking()
            .Where(x => x.WeekId == weekId && x.Status == ProductionDailyCaptureStatus.Active && x.EffectiveDate <= through && !excludedCaptures.Contains(x.Id))
            .GroupBy(x => new { x.ProductId, x.EffectiveDate, x.Area, x.ShiftId, Shift = x.Shift.Name })
            .Select(x => new { x.Key.ProductId, x.Key.EffectiveDate, x.Key.Area, x.Key.ShiftId, x.Key.Shift, Quantity = x.Sum(c => c.Quantity) })
            .ToListAsync(token);
        if (scenario is not null)
            foreach (var addition in scenario.Additions)
            {
                var shift = await db.ProductionShifts.AsNoTracking().SingleAsync(x => x.Id == addition.ShiftId, token);
                var index = shifts.FindIndex(x => x.ProductId == addition.ProductId && x.EffectiveDate == scenario.Date && x.Area == addition.Area && x.ShiftId == addition.ShiftId);
                var quantity = addition.Quantity + (index < 0 ? 0 : shifts[index].Quantity);
                if (index >= 0) shifts.RemoveAt(index);
                shifts.Add(new { ProductId = addition.ProductId, EffectiveDate = scenario.Date, Area = addition.Area, ShiftId = addition.ShiftId, Shift = shift.Name, Quantity = quantity });
            }
        var explicitCarry = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => x.Id == weekId).Select(x => x.ExplicitCarryover).SingleAsync(token);
        var intentions = await db.ProductionCarryoverPlans.AsNoTracking().Where(x => x.WeekId == weekId && !explicitCarry)
            .Select(x => new { x.ProductId, x.PlannedDate, x.Area, x.Quantity }).ToListAsync(token);
        if (explicitCarry)
            intentions.AddRange(await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.WeekId == weekId && x.Quantity > 0)
                .GroupBy(x => new { x.ProductId, x.Area }).Select(x => new { x.Key.ProductId, PlannedDate = balance.WeekStart, x.Key.Area, Quantity = x.Sum(o => o.Quantity) }).ToListAsync(token));
        var grouped = balance.Rows.GroupBy(x => x.ProductId).ToDictionary(x => x.Key, x => x.OrderBy(r => r.Date).ToArray());
        // An intention can remain after its physical balance was consumed; keep it visible without adding stock.
        var missingIds = intentions.Select(x => x.ProductId).Except(grouped.Keys).ToArray();
        var missing = await db.Products.AsNoTracking().Where(x => missingIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Sku, x.Description }).ToListAsync(token);
        foreach (var product in missing)
            grouped[product.Id] = Enumerable.Range(0, ProductionWeekCalendar.DayCount).Select(i => new ProductionDailyBalanceRow(
                balance.WeekStart.AddDays(i), product.Id, product.Sku, product.Description, 0, 0, 0, 0, 0, [],
                new(ProductionDailyArea.Cutting, false, 0, 0, 0), new(ProductionDailyArea.Sewing, false, 0, 0, 0),
                new(ProductionDailyArea.ReadyToPack, false, 0, 0, 0), 0)).ToArray();
        var products = new List<ProductionWeeklyProduct>();
        foreach (var (productId, days) in grouped)
        {
            var row = days.Last(x => x.Date <= through);
            if (!string.IsNullOrWhiteSpace(filter.Sku) &&
                !row.Sku.Contains(filter.Sku.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !(row.Description?.Contains(filter.Sku.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            if (!string.IsNullOrWhiteSpace(filter.Reference) && !days.SelectMany(x => x.References)
                .Any(x => x.Contains(filter.Reference.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            if (filter.Area is { } area && !(area switch
                { ProductionDailyArea.Cutting => row.Cutting, ProductionDailyArea.Sewing => row.Sewing, _ => row.ReadyToPack }).Applies) continue;
            var planned = days.Sum(x => x.NewPlan);
            var work = intentions.Where(x => x.ProductId == productId).OrderBy(x => x.PlannedDate).ThenBy(x => x.Area)
                .Select(x => new ProductionWeeklyIntention(x.PlannedDate, x.Area, x.Quantity)).ToArray();
            var areas = new[] { row.Cutting, row.Sewing, row.ReadyToPack };
            if (planned == 0 && work.Length == 0 && days.All(x => x.Carryover == 0) &&
                areas.All(x => x.Opening == 0 && x.Completed == 0 && x.Pending == 0 && x.ToReconcile == 0 && x.Extra == 0)) continue;
            products.Add(new(productId, row.Sku, row.Description, planned, row.Cutting, row.Sewing, row.ReadyToPack,
                days.Select(day => new ProductionWeeklyDay(day.Date, day.NewPlan,
                    shifts.Where(x => x.ProductId == productId && x.EffectiveDate == day.Date)
                        .OrderBy(x => x.Area).ThenBy(x => x.Shift).ThenBy(x => x.ShiftId)
                        .Select(x => new ProductionWeeklyShift(x.Area, x.ShiftId, x.Shift, x.Quantity)).ToArray(), day)).ToArray(), work));
        }
        return new(weekId, balance.WeekStart, balance.WeekEnd, through, balance.Status,
            products.OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ProductId).ToArray());
    }
}
