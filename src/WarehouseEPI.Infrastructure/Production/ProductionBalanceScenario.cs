using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionBalanceScenario(DateOnly Date, IReadOnlyList<Guid> ReversedCaptures,
    IReadOnlyList<ProductionBalanceAddition> Additions);
public sealed record ProductionBalanceAddition(Guid Id, Guid ProductId, ProductionDailyArea Area, Guid ShiftId, decimal Quantity, string? Notes = null);

public sealed partial class ProductionDailyBalanceService
{
    private sealed record BalanceCapture(Guid Id, Guid ProductId, ProductionDailyArea Area, DateOnly EffectiveDate,
        Guid ShiftId, decimal Quantity, bool IsFlexible, Guid WeekId, decimal Allocated, bool HasAllocations, DateTimeOffset RecordedAt);
    private sealed record BalanceAllocation(Guid CaptureId, Guid ScheduleLineId, Guid WorkOrderStageId,
        DateOnly EffectiveDate, Guid ShiftId, decimal Quantity);

    // Detached read models only: no EF tracking, writes, provisional orders or database transactions.
    private static void ApplyScenario(ProductionBalanceScenario scenario, ProductionScheduleWeek week,
        ProductionDailyConfiguration config, List<ProductionScheduleLine> lines, List<BalanceCapture> captures,
        List<BalanceAllocation> allocations)
    {
        var reversedCutting = captures.Where(x => x.Area == ProductionDailyArea.Cutting && scenario.ReversedCaptures.Contains(x.Id)).Select(x => x.Id).ToHashSet();
        var cancelledExtras = allocations.Where(a => reversedCutting.Contains(a.CaptureId))
            .Select(a => a.ScheduleLineId).ToHashSet();
        lines.RemoveAll(x => x.IsExtra && cancelledExtras.Contains(x.Id));
        captures.RemoveAll(x => scenario.ReversedCaptures.Contains(x.Id));
        allocations.RemoveAll(x => scenario.ReversedCaptures.Contains(x.CaptureId));
        Guid Stage(ProductionDailyArea area) => ProductionDailyProcessFlow.Stage(config, area)!.Value;
        decimal Used(Guid lineId, Guid stageId) => allocations.Where(a => a.ScheduleLineId == lineId && a.WorkOrderStageId == stageId).Sum(a => a.Quantity);
        IEnumerable<ProductionScheduleLine> Ordered(Guid productId) => lines.Where(x => x.ProductId == productId && !x.IsCancelled && x.WorkOrder is not null &&
                x.WorkOrder.Status is ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress)
            .OrderBy(x => x.Week.WeekStart).ThenBy(x => x.IsCarryover ? 0 : 1).ThenBy(x => x.PlannedDate).ThenBy(x => x.Sequence).ThenBy(x => x.Id);
        foreach (var addition in scenario.Additions)
        {
            if (addition.Area == ProductionDailyArea.Cutting)
            {
                var capacity = Ordered(addition.ProductId).Sum(line =>
                {
                    var stage = line.WorkOrder!.Stages.SingleOrDefault(s => s.SourceStageId == Stage(addition.Area));
                    return stage is null ? 0 : Math.Max(0, line.Quantity - Used(line.Id, stage.Id));
                });
                var extra = Math.Max(0, addition.Quantity - capacity);
                if (extra > 0)
                {
                    var stages = Enum.GetValues<ProductionDailyArea>().Select((area, i) => new ProductionWorkOrderStage
                    { Id = Guid.NewGuid(), SourceStageId = Stage(area), Sequence = i + 1, Code = area.ToString(), Name = area.ToString() }).ToArray();
                    var order = new ProductionWorkOrder { Id = addition.Id, Number = "PREVIEW", CreateFingerprint = "PREVIEW", Status = ProductionWorkOrderStatus.Released, Stages = stages };
                    lines.Add(new ProductionScheduleLine { Id = addition.Id, WeekId = week.Id, Week = week, ProductId = addition.ProductId,
                        Quantity = extra, PlannedDate = scenario.Date, Sequence = lines.Where(x => x.WeekId == week.Id).Select(x => x.Sequence).DefaultIfEmpty().Max() + 1, IsExtra = true, WorkOrderId = order.Id, WorkOrder = order });
                }
            }
            captures.Add(new(addition.Id, addition.ProductId, addition.Area, scenario.Date, addition.ShiftId, addition.Quantity, true, week.Id, 0, false, captures.Select(x => x.RecordedAt).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Max().AddTicks(1)));
            // Reconcile the same product from upstream to downstream after each addition, as confirmation does.
            foreach (var capture in captures.Where(x => x.ProductId == addition.ProductId && x.IsFlexible)
                         .OrderBy(x => x.Area).ThenBy(x => x.EffectiveDate).ThenBy(x => x.RecordedAt).ThenBy(x => x.Id).ToArray())
            {
                var remaining = capture.Quantity - allocations.Where(a => a.CaptureId == capture.Id).Sum(a => a.Quantity);
                foreach (var line in Ordered(capture.ProductId))
                {
                    if (remaining <= 0) break;
                    var stages = line.WorkOrder!.Stages.OrderBy(s => s.Sequence).ToArray();
                    var index = Array.FindIndex(stages, s => s.SourceStageId == Stage(capture.Area));
                    if (index < 0) continue;
                    var used = Used(line.Id, stages[index].Id);
                    var input = index == 0 ? line.Quantity : Used(line.Id, stages[index - 1].Id);
                    var amount = Math.Min(remaining, Math.Max(0, Math.Min(line.Quantity, input) - used));
                    if (amount == 0) continue;
                    allocations.Add(new(capture.Id, line.Id, stages[index].Id, capture.EffectiveDate, capture.ShiftId, amount));
                    remaining -= amount;
                }
            }
        }
        for (var i = 0; i < captures.Count; i++)
        {
            var total = allocations.Where(a => a.CaptureId == captures[i].Id).Sum(a => a.Quantity);
            captures[i] = captures[i] with { Allocated = total, HasAllocations = total > 0 };
        }
    }
}
