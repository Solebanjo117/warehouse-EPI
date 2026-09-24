using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionSchedulePlanSummaryRow(DateOnly Day, string Unit,
    decimal NewQuantity, decimal CustomerQuantity, decimal StockQuantity,
    int NewLines, int CustomerLines, int StockLines,
    decimal ImportedOpening, decimal PlannedCarryover);

public sealed partial class ProductionDailyScheduleService
{
    public async Task<IReadOnlyList<ProductionSchedulePlanSummaryRow>> GetPlanSummaryAsync(
        Guid weekId, CancellationToken token = default)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking()
            .Include(x => x.Lines).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
            .SingleOrDefaultAsync(x => x.Id == weekId, token);
        if (week is null) return [];
        var carryover = await db.ProductionCarryoverPlans.AsNoTracking()
            .Where(x => x.WeekId == weekId && x.Quantity > 0 && !week.ExplicitCarryover)
            .Select(x => new { x.PlannedDate, x.Quantity, x.ProductId }).ToArrayAsync(token);
        var carryProductIds = carryover.Select(x => x.ProductId).Distinct().ToArray();
        var carryProducts = await db.Products.AsNoTracking().Where(x => carryProductIds.Contains(x.Id))
            .Select(x => new { x.Id, x.BaseUnit.Code }).ToDictionaryAsync(x => x.Id, x => x.Code, token);
        var units = week.Lines.Where(x => !x.IsCancelled && !x.IsExtra).Select(x => x.Product.BaseUnit.Code)
            .Concat(carryProducts.Values).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (units.Length == 0) units = ["—"];
        var rows = new List<ProductionSchedulePlanSummaryRow>();
        foreach (var day in ProductionWeekCalendar.Days(week.WeekStart))
            foreach (var unit in units)
            {
                var lines = week.Lines.Where(x => !x.IsCancelled && !x.IsExtra && x.PlannedDate == day &&
                    x.Product.BaseUnit.Code == unit).ToArray();
                var fresh = lines.Where(x => !x.IsCarryover).ToArray();
                var customers = fresh.Where(x => HasOrder(x.OrderReference1, x.OrderReference2, x.OrderReference3)).ToArray();
                rows.Add(new(day, unit, fresh.Sum(x => x.Quantity), customers.Sum(x => x.Quantity),
                    fresh.Sum(x => x.Quantity) - customers.Sum(x => x.Quantity), fresh.Length, customers.Length,
                    fresh.Length - customers.Length, lines.Where(x => x.IsCarryover).Sum(x => x.Quantity),
                    carryover.Where(x => x.PlannedDate == day && carryProducts.GetValueOrDefault(x.ProductId) == unit)
                        .Sum(x => x.Quantity)));
            }
        return rows;
    }

    public async Task<IReadOnlyList<string>> GetPublicationIssuesAsync(Guid weekId, CancellationToken token = default)
    {
        var week = await db.ProductionScheduleWeeks.Include(x => x.Lines).ThenInclude(x => x.Product)
            .SingleOrDefaultAsync(x => x.Id == weekId, token);
        if (week is null) return ["La semana ya no existe."];
        var issues = await ValidateForPublicationAsync(week, token);
        if (week.ExplicitCarryover) issues.AddRange(await new ProductionWeekOpeningService(db).RevalidateAsync(weekId, token));
        return issues;
    }

    private static bool HasOrder(params string?[] references) => references.Any(x => !string.IsNullOrWhiteSpace(x));
}
