using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyBalanceService
{
    private async Task<ProductionDailyBalanceView> ExplicitWeekAsync(ProductionScheduleWeek week,
        DateOnly? cutoff, Guid? shiftId, ProductionBalanceScenario? scenario, CancellationToken token)
    {
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var lines = await db.ProductionScheduleLines.AsNoTracking().Include(x => x.WorkOrder).ThenInclude(x => x!.Stages)
            .Where(x => x.WeekId == week.Id && !x.IsCancelled && !x.IsExtra).ToListAsync(token);
        var openings = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.WeekId == week.Id && x.Quantity > 0).ToListAsync(token);
        var captures = await db.ProductionDailyCaptures.AsNoTracking()
            .Where(x => x.WeekId == week.Id && x.Status == ProductionDailyCaptureStatus.Active)
            .Select(x => new { x.Id, x.ProductId, x.Area, Date = x.EffectiveDate, x.ShiftId, x.Quantity,
                Residual = x.Quantity - x.Allocations.Sum(a => a.Quantity) }).ToListAsync(token);
        if (scenario is not null)
        {
            var residuals = await ExplicitScenarioResidualsAsync(week, config, scenario, token);
            captures = captures.Select(x => new { x.Id, x.ProductId, x.Area, x.Date, x.ShiftId, x.Quantity,
                Residual = residuals.GetValueOrDefault(x.Id, x.Residual) }).ToList();
            foreach (var change in scenario.Openings ?? [])
            {
                openings.RemoveAll(x => x.SourceWeekId == change.SourceWeekId && x.SourceLineId == change.SourceLineId && x.Area == change.Area);
                var productId = await db.ProductionScheduleLines.Where(x => x.Id == change.SourceLineId).Select(x => x.ProductId).SingleAsync(token);
                if (change.Quantity > 0) openings.Add(new() { WeekId = week.Id, SourceWeekId = change.SourceWeekId,
                    SourceLineId = change.SourceLineId, ProductId = productId, Area = change.Area, Quantity = change.Quantity });
            }
            captures.RemoveAll(x => scenario.ReversedCaptures.Contains(x.Id));
            foreach (var addition in scenario.Additions)
                captures.Add(new { addition.Id, addition.ProductId, addition.Area, Date = scenario.Date,
                    addition.ShiftId, addition.Quantity, Residual = residuals.GetValueOrDefault(addition.Id) });
            foreach (var line in lines)
                if (scenario.PlanQuantities?.TryGetValue(line.Id, out var quantity) == true) line.Quantity = quantity;
            foreach (var addition in scenario.NewPlans ?? [])
                lines.Add(new() { Id = addition.OperationId, WeekId = week.Id, ProductId = addition.ProductId,
                    Quantity = addition.Requested, PlannedDate = scenario.Date });
        }
        if (cutoff.HasValue) captures.RemoveAll(x => x.Date == cutoff && x.ShiftId != shiftId);
        var ids = lines.Select(x => x.ProductId).Concat(openings.Select(x => x.ProductId)).Concat(captures.Select(x => x.ProductId)).Distinct().ToArray();
        var sourceIds = openings.Select(x => x.SourceLineId).Distinct().ToArray();
        var sources = await db.ProductionScheduleLines.AsNoTracking().Where(x => sourceIds.Contains(x.Id)).ToListAsync(token);
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        var routes = await db.ProductionRoutes.AsNoTracking().Include(x => x.Stages)
            .Where(x => ids.Contains(x.ProductId) && x.IsActive).ToDictionaryAsync(x => x.ProductId, token);
        var result = new List<ProductionDailyBalanceRow>();
        foreach (var productId in ids)
        {
            var product = products[productId];
            var own = lines.Where(x => x.ProductId == productId).ToArray();
            var carried = openings.Where(x => x.ProductId == productId).ToArray();
            var done = captures.Where(x => x.ProductId == productId).ToArray();
            bool Applies(ProductionScheduleLine line, ProductionDailyArea area)
            {
                var stages = line.WorkOrder?.Stages.OrderBy(x => x.Sequence).Select(x => x.SourceStageId)
                    ?? routes.GetValueOrDefault(productId)?.Stages.OrderBy(x => x.Sequence).Select(x => x.StageId) ?? [];
                return ProductionDailyProcessFlow.Resolve(config, stages, line.IsCarryover ? line.StartArea : null).Contains(area);
            }
            foreach (var day in ProductionWeekCalendar.Days(week.WeekStart))
            {
                ProductionDailyAreaBalance Area(ProductionDailyArea area)
                {
                    var initial = carried.Where(x => x.Area == area).Sum(x => x.Quantity);
                    var plan = own.Where(x => x.PlannedDate <= day && Applies(x, area)).Sum(x => x.Quantity);
                    var complete = done.Where(x => x.Area == area && x.Date <= day).Sum(x => x.Quantity);
                    var net = initial + plan - complete;
                    var advance = Math.Min(Math.Max(0, -net), own.Where(x => x.PlannedDate > day && Applies(x, area)).Sum(x => x.Quantity));
                    var applies = own.Any(x => Applies(x, area)) || carried.Any(x => x.Area == area) || done.Any(x => x.Area == area);
                    return new(area, applies, complete, Math.Max(0, net), advance, Math.Max(0, -net) - advance,
                        area == ProductionDailyArea.Cutting ? 0 : done.Where(x => x.Area == area && x.Date <= day).Sum(x => x.Residual), initial, net);
                }
                var finalArea = Enum.GetValues<ProductionDailyArea>().LastOrDefault(a => Area(a).Applies);
                var target = carried.Where(x => x.Area == finalArea).Sum(x => x.Quantity) + own.Where(x => Applies(x, finalArea)).Sum(x => x.Quantity);
                var progress = target > 0 ? Math.Min(100, Area(finalArea).Completed / target * 100) : 0;
                result.Add(new(day, productId, product.Sku, product.Description,
                    own.Where(x => x.PlannedDate == day && !x.IsCarryover).Sum(x => x.Quantity),
                    0,
                    own.Where(x => x.PlannedDate <= day && !x.IsCarryover).Sum(x => x.Quantity),
                    done.Where(x => x.Date == day && x.ShiftId == config.Shift1Id).Sum(x => x.Quantity),
                    done.Where(x => x.Date == day && x.ShiftId == config.Shift2Id).Sum(x => x.Quantity),
                    own.Concat(sources.Where(x => carried.Any(o => o.SourceLineId == x.Id))).SelectMany(References).Distinct().ToArray(), Area(ProductionDailyArea.Cutting),
                    Area(ProductionDailyArea.Sewing), Area(ProductionDailyArea.ReadyToPack), progress));
            }
        }
        return new(week.Id, week.WeekStart, week.WeekEnd, week.Status, result);
    }

    private async Task<Dictionary<Guid, decimal>> ExplicitScenarioResidualsAsync(ProductionScheduleWeek week,
        ProductionDailyConfiguration config, ProductionBalanceScenario scenario, CancellationToken token)
    {
        if (scenario.Additions.Count == 0 && scenario.ReversedCaptures.Count == 0) return [];
        var admitted = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.WeekId == week.Id && x.Quantity > 0).ToListAsync(token);
        var ids = admitted.Select(x => x.SourceLineId).Distinct().ToArray();
        var lines = await db.ProductionScheduleLines.AsNoTracking().Include(x => x.Week).Include(x => x.WorkOrder).ThenInclude(x => x!.Stages)
            .Where(x => !x.IsCancelled && (x.WeekId == week.Id || ids.Contains(x.Id))).ToListAsync(token);
        var lineIds = lines.Select(x => x.Id).ToArray();
        var forwarded = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.SourceWeekId == week.Id && x.Quantity > 0).ToListAsync(token);
        var limits = lines.SelectMany(line => Enum.GetValues<ProductionDailyArea>().Select(area =>
            new { Key = (line.Id, area), Quantity = (line.WeekId == week.Id ? scenario.PlanQuantities?.GetValueOrDefault(line.Id, line.Quantity) ?? line.Quantity
                : admitted.Where(o => o.SourceLineId == line.Id && o.Area == area).Sum(o => o.Quantity)) - forwarded.Where(o => o.SourceLineId == line.Id && o.Area == area).Sum(o => o.Quantity) }))
            .ToDictionary(x => x.Key, x => x.Quantity);
        var captures = await db.ProductionDailyCaptures.AsNoTracking().Where(x => x.Status == ProductionDailyCaptureStatus.Active &&
            (x.WeekId == week.Id || x.Allocations.Any(a => lineIds.Contains(a.ScheduleLineId))))
            .Select(x => new BalanceCapture(x.Id, x.ProductId, x.Area, x.EffectiveDate, x.ShiftId, x.Quantity, x.IsFlexible,
                x.WeekId, x.Allocations.Sum(a => a.Quantity), x.Allocations.Any(), x.RecordedAt)).ToListAsync(token);
        var allocations = await db.ProductionDailyCaptureAllocations.AsNoTracking().Where(x => lineIds.Contains(x.ScheduleLineId) && x.Capture.Status == ProductionDailyCaptureStatus.Active)
            .Select(x => new BalanceAllocation(x.CaptureId, x.ScheduleLineId, x.WorkOrderStageId, x.Capture.EffectiveDate, x.Capture.ShiftId, x.Quantity)).ToListAsync(token);
        ApplyScenario(scenario, week, config, lines, captures, allocations, limits);
        return captures.Where(x => x.WeekId == week.Id).ToDictionary(x => x.Id, x => x.Quantity - x.Allocated);
    }
}
