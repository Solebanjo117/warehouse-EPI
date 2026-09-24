using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyBalanceService(WarehouseDbContext db)
{
    public Task<ProductionDailyBalanceView?> GetAsync(Guid weekId, CancellationToken token = default) => GetAsync(weekId, false, false, token);

    internal async Task<ProductionDailyBalanceView?> GetAsync(Guid weekId, bool availability, bool priorOnly, CancellationToken token,
        DateOnly? shiftCutoffDate = null, Guid? includedShiftId = null, ProductionBalanceScenario? scenario = null)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weekId, token);
        if (week is null) return null;
        if (week.ExplicitCarryover && !availability && !priorOnly)
            return await ExplicitWeekAsync(week, shiftCutoffDate, includedShiftId, scenario, token);
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var lines = await db.ProductionScheduleLines.AsNoTracking()
            .Include(x => x.Week).Include(x => x.WorkOrder).ThenInclude(x => x!.Stages)
            .Where(x => !x.IsCancelled && (x.WeekId == weekId ||
                x.WorkOrderId != null && x.Week.WeekStart < week.WeekStart && x.Week.Status != ProductionScheduleWeekStatus.Draft ||
                x.CaptureAllocations.Any(a => a.Capture.IsFlexible && a.Capture.EffectiveDate <= week.WeekEnd)))
            .ToListAsync(token);
        if (availability) lines = lines.Where(x => x.WorkOrderId != null && x.Week.Status != ProductionScheduleWeekStatus.Draft &&
            (!priorOnly || x.Week.WeekStart < week.WeekStart) &&
            x.WorkOrder!.Status is ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress).ToList();
        var captures = await db.ProductionDailyCaptures.AsNoTracking()
            .Where(x => (x.WeekId == weekId || x.IsFlexible && x.EffectiveDate <= week.WeekEnd) && x.Status == ProductionDailyCaptureStatus.Active)
                        .Select(x => new BalanceCapture(x.Id, x.ProductId, x.Area, x.EffectiveDate, x.ShiftId, x.Quantity, x.IsFlexible, x.WeekId, x.Allocations.Sum(a => a.Quantity), x.Allocations.Any(), x.RecordedAt))
            .ToListAsync(token);
        var lineIds = lines.Select(x => x.Id).ToArray();
        var allocations = await db.ProductionDailyCaptureAllocations.AsNoTracking()
            .Where(x => lineIds.Contains(x.ScheduleLineId) && x.Capture.Status == ProductionDailyCaptureStatus.Active &&
                x.Capture.EffectiveDate <= week.WeekEnd)
                        .Select(x => new BalanceAllocation(x.CaptureId, x.ScheduleLineId, x.WorkOrderStageId, x.Capture.EffectiveDate, x.Capture.ShiftId, x.Quantity))
            .ToListAsync(token);
        if (scenario is not null) ApplyScenario(scenario, week, config, lines, captures, allocations);
        if (shiftCutoffDate is not null)
        {
            captures = captures.Where(x => x.EffectiveDate != shiftCutoffDate || x.ShiftId == includedShiftId).ToList();
            allocations = allocations.Where(x => x.EffectiveDate != shiftCutoffDate || x.ShiftId == includedShiftId).ToList();
        }
        var byLine = allocations.ToLookup(x => x.ScheduleLineId);
        var productIds = lines.Select(x => x.ProductId).Concat(captures.Select(x => x.ProductId)).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, token);
        var routes = await db.ProductionRoutes.AsNoTracking().Include(x => x.Stages)
            .Where(x => productIds.Contains(x.ProductId) && x.IsActive).ToDictionaryAsync(x => x.ProductId, token);
        var areas = Enum.GetValues<ProductionDailyArea>();
        var rows = new List<ProductionDailyBalanceRow>();
        foreach (var productId in productIds.OrderBy(x => products[x].Sku, StringComparer.OrdinalIgnoreCase).ThenBy(x => x))
        {
            var product = products[productId];
            var productLines = lines.Where(x => x.ProductId == productId).ToArray();
            var currentLines = productLines.Where(x => x.WeekId == weekId && !x.IsExtra).ToArray();
            var productCaptures = captures.Where(x => x.ProductId == productId).ToArray();
            var legacyCaptures = productCaptures.Where(x => !x.HasAllocations && !x.IsFlexible).ToArray();
            var routeStages = routes.GetValueOrDefault(productId)?.Stages.OrderBy(x => x.Sequence).Select(x => x.StageId).ToArray() ?? [];
            // Imported history has no order/allocation links. Keep its aggregate calculation separate from live orders.
            var legacyLines = legacyCaptures.Length == 0 ? [] : currentLines.Where(x => !x.WorkOrderId.HasValue).ToArray();
            var legacyIds = legacyLines.Select(x => x.Id).ToHashSet();
            var flows = productLines.Where(x => !legacyIds.Contains(x.Id)).Select(line =>
            {
                var stages = line.WorkOrder is null
                    ? ProductionDailyProcessFlow.Resolve(config, [], line.IsCarryover ? line.StartArea : null)
                        .Select(area => new FlowStage(area, null)).ToArray()
                    : line.WorkOrder.Stages.OrderBy(x => x.Sequence)
                        .SelectMany(stage => areas.Where(area => ProductionDailyProcessFlow.Stage(config, area) == stage.SourceStageId)
                            .Select(area => new FlowStage(area, stage.Id))).ToArray();
                return (Line: line, Stages: stages);
            }).ToArray();
            var legacyAreas = legacyLines.SelectMany(line => ProductionDailyProcessFlow.Resolve(config, routeStages,
                    line.IsCarryover ? line.StartArea : null))
                .Concat(legacyCaptures.Length == 0 ? [] : ProductionDailyProcessFlow.Resolve(config, routeStages, null))
                .Concat(legacyCaptures.Select(x => x.Area)).Distinct().Order().ToArray();
            var applicable = flows.SelectMany(x => x.Stages.Select(s => s.Area)).Concat(legacyAreas)
                .Concat(productCaptures.Select(x => x.Area)).ToHashSet();
            foreach (var day in Enumerable.Range(0, ProductionWeekCalendar.DayCount).Select(week.WeekStart.AddDays))
            {
                var dayLines = currentLines.Where(x => x.PlannedDate == day).ToArray();
                var cumulativeLines = currentLines.Where(x => x.PlannedDate <= day).ToArray();
                var completed = new decimal[3];
                var pending = new decimal[3];
                var advance = new decimal[3];
                var extras = new decimal[3];
                var openingByArea = new decimal[3];
                decimal progressTarget = 0, finalCompleted = 0;
                foreach (var flow in flows)
                {
                    var lineCaptures = byLine[flow.Line.Id].ToArray();
                    decimal Total(FlowStage stage, DateOnly through) => lineCaptures
                        .Where(x => x.WorkOrderStageId == stage.Id && x.EffectiveDate <= through).Sum(x => x.Quantity);
                    var before = flow.Stages.Select(stage => Total(stage, week.WeekStart.AddDays(-1))).ToArray();
                    var through = flow.Stages.Select(stage => Total(stage, day)).ToArray();
                    var isPrior = flow.Line.Week.WeekStart < week.WeekStart;
                    var planned = isPrior || flow.Line.PlannedDate <= day || availability && !flow.Line.IsExtra && flow.Line.WeekId == weekId ? flow.Line.Quantity : 0;
                    decimal openingTotal = 0;
                    for (var index = 0; index < flow.Stages.Length; index++)
                    {
                        var area = (int)flow.Stages[index].Area;
                        // Extra capacity is never a plan to cut more pieces, including when viewing an earlier date.
                        var firstInput = flow.Line.IsExtra && index == 0 ? through[index] : planned;
                        var opening = isPrior ? (index == 0 ? flow.Line.Quantity : before[index - 1]) - before[index] : 0;
                        openingTotal += opening;
                        // Older captures can be reconciled to a new week's order. Their signed opening survives that link.
                        openingByArea[area] += (isPrior ? opening : (index == 0 ? 0 : before[index - 1]) - before[index])
                            + (!isPrior && flow.Line.IsCarryover && index == 0 ? flow.Line.Quantity : 0);
                        var done = through[index] - before[index];
                        completed[area] += done;
                        pending[area] += (index == 0 ? firstInput : through[index - 1]) - through[index];
                        if (flow.Line.IsExtra) extras[area] += done;
                        advance[area] += Math.Max(0, done - (isPrior ? Math.Max(0, flow.Line.Quantity - before[index]) : planned));
                    }
                    progressTarget += flow.Line.IsExtra ? 0 : isPrior ? openingTotal : planned;
                    if (flow.Stages.Length > 0) finalCompleted += through[^1] - before[^1];
                }
                // The unapplied part is real production, not imported history. Its signed balance survives week boundaries.
                foreach (var capture in productCaptures.Where(x => x.IsFlexible && x.EffectiveDate <= day))
                {
                    var residual = capture.Quantity - capture.Allocated;
                    var area = (int)capture.Area;
                    pending[area] -= residual;
                    if (capture.EffectiveDate < week.WeekStart)
                    {
                        openingByArea[area] -= residual;
                        if (area < 2) openingByArea[area + 1] += residual;
                    }
                    applicable.Add(capture.Area);
                    if (area < 2) { pending[area + 1] += residual; applicable.Add((ProductionDailyArea)(area + 1)); }
                    if (capture.EffectiveDate >= week.WeekStart)
                    {
                        completed[area] += residual;
                        if (area == 2) finalCompleted += residual;
                    }
                }
                var legacyPlan = legacyLines.Where(x => x.PlannedDate <= day).ToArray();
                var legacyTarget = legacyPlan.Sum(x => x.Quantity);
                var legacyDone = areas.ToDictionary(area => area,
                    area => legacyCaptures.Where(x => x.Area == area && x.EffectiveDate <= day).Sum(x => x.Quantity));
                for (var index = 0; index < legacyAreas.Length; index++)
                {
                    var area = legacyAreas[index];
                    var direct = legacyPlan.Where(line => (line.StartArea ?? legacyAreas[0]) == area).Sum(x => x.Quantity);
                    var input = direct + (index == 0 ? 0 : legacyDone[legacyAreas[index - 1]]);
                    openingByArea[(int)area] += legacyLines.Where(line => line.IsCarryover && (line.StartArea ?? legacyAreas[0]) == area).Sum(line => line.Quantity);
                    completed[(int)area] += legacyDone[area];
                    pending[(int)area] += Math.Max(0, input - legacyDone[area]);
                    advance[(int)area] += Math.Max(0, legacyDone[area] - legacyTarget);
                }
                progressTarget += legacyTarget;
                if (legacyAreas.Length > 0) finalCompleted += legacyDone[legacyAreas[^1]];
                var progress = progressTarget <= 0 ? (finalCompleted > 0 ? 100m : 0m) :
                    decimal.Round(Math.Min(100m, finalCompleted * 100m / progressTarget), 1);
                ProductionDailyAreaBalance Area(ProductionDailyArea area) => new(area, applicable.Contains(area),
                    completed[(int)area], Math.Max(0, pending[(int)area]), advance[(int)area],
                    extras[(int)area], area == ProductionDailyArea.Cutting ? 0 : Math.Max(0, -pending[(int)area]), openingByArea[(int)area], pending[(int)area]);
                var dayCaptures = productCaptures.Where(x => x.EffectiveDate == day).ToArray();
                rows.Add(new(day, productId, product.Sku, product.Description,
                    dayLines.Where(x => !x.IsCarryover).Sum(x => x.Quantity), dayLines.Where(x => x.IsCarryover).Sum(x => x.Quantity),
                    cumulativeLines.Where(x => !x.IsCarryover).Sum(x => x.Quantity),
                    dayCaptures.Where(x => x.ShiftId == config.Shift1Id).Sum(x => x.Quantity),
                    dayCaptures.Where(x => x.ShiftId == config.Shift2Id).Sum(x => x.Quantity),
                    cumulativeLines.SelectMany(References).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Area(ProductionDailyArea.Cutting), Area(ProductionDailyArea.Sewing), Area(ProductionDailyArea.ReadyToPack), progress));
            }
        }
        return new(week.Id, week.WeekStart, week.WeekEnd, week.Status, rows);
    }

    private sealed record FlowStage(ProductionDailyArea Area, Guid? Id);
    public async Task<IReadOnlyList<ProductionDailyCaptureDetail>> GetCaptureDetailsAsync(
        Guid weekId, DateOnly? day = null, Guid? productId = null, ProductionDailyArea? area = null,
        Guid? shiftId = null, CancellationToken token = default)
    {
        var query = db.ProductionDailyCaptures.AsNoTracking().Where(x => x.WeekId == weekId)
            .Include(x => x.Shift).Include(x => x.Product).Include(x => x.ResponsibleUser)
            .Include(x => x.Allocations).ThenInclude(x => x.WorkOrder).AsQueryable();
        if (day.HasValue) query = query.Where(x => x.EffectiveDate == day);
        if (productId.HasValue) query = query.Where(x => x.ProductId == productId);
        if (area.HasValue) query = query.Where(x => x.Area == area);
        if (shiftId.HasValue) query = query.Where(x => x.ShiftId == shiftId);
        return await query.OrderByDescending(x => x.RecordedAt).Take(500).Select(x =>
            new ProductionDailyCaptureDetail(x.Id, x.EffectiveDate, x.Area, x.Shift.Name, x.Product.Sku,
                x.Quantity, x.ImportedReporter ?? x.ResponsibleUser.FullName, x.RecordedAt, x.Status,
                x.Notes, x.Allocations.Select(a => a.WorkOrder.Number).Distinct().OrderBy(n => n).ToArray()))
            .ToListAsync(token);
    }

    private static IEnumerable<string> References(ProductionScheduleLine line) =>
        new[] { line.OrderReference1, line.OrderReference2, line.OrderReference3 }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!);
}

