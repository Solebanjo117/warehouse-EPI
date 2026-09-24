using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyCaptureService
{
    private async Task<IReadOnlyList<ProductionDailyAllocationPreview>> AllocateAsync(Guid productId, Guid stageId,
        ProductionDailyArea area, decimal quantityRequested, DateOnly throughWeek, CancellationToken token, Guid? admissionWeekId = null)
    {
        var explicitWeek = admissionWeekId.HasValue && await db.ProductionScheduleWeeks.AsNoTracking()
            .AnyAsync(x => x.Id == admissionWeekId && x.ExplicitCarryover, token);
        var admissions = explicitWeek ? await db.ProductionWeekOpenings.AsNoTracking()
            .Where(x => x.WeekId == admissionWeekId && x.ProductId == productId && x.Area == area && x.Quantity > 0)
            .GroupBy(x => x.SourceLineId).Select(x => new { Id = x.Key, Quantity = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Quantity, token) : new Dictionary<Guid, decimal>();
        var admittedIds = admissions.Keys.ToArray();
        var lines = await db.ProductionScheduleLines.AsNoTracking()
            .Include(x => x.Week).Include(x => x.WorkOrder).ThenInclude(x => x!.Stages)
            .Include(x => x.WorkOrder).ThenInclude(x => x!.Batches)
            .Include(x => x.WorkOrder).ThenInclude(x => x!.Events)
            .Include(x => x.WorkOrder).ThenInclude(x => x!.Batches).ThenInclude(x => x.Results)
            .Where(x => !x.IsCancelled && x.ProductId == productId && x.WorkOrderId != null &&
                x.Week.Status != ProductionScheduleWeekStatus.Draft && x.Week.WeekStart <= throughWeek &&
                (explicitWeek ? x.WeekId == admissionWeekId || admittedIds.Contains(x.Id) : !x.Week.ExplicitCarryover))
            .OrderBy(x => x.Week.WeekStart).ThenBy(x => x.IsCarryover ? 0 : 1)
            .ThenBy(x => x.PlannedDate).ThenBy(x => x.Sequence).ThenBy(x => x.Id).ToListAsync(token);
        var lineIds = lines.Select(x => x.Id).ToArray();
        var used = await db.ProductionDailyCaptureAllocations.AsNoTracking()
            .Where(x => lineIds.Contains(x.ScheduleLineId) && x.Capture.Status == ProductionDailyCaptureStatus.Active &&
                        x.WorkOrderStage.SourceStageId == stageId)
            .GroupBy(x => x.ScheduleLineId).Select(x => new { LineId = x.Key, Quantity = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.LineId, x => x.Quantity, token);
        var remaining = quantityRequested;
        var weeklyUsed = explicitWeek ? await db.ProductionDailyCaptureAllocations.AsNoTracking()
            .Where(x => lineIds.Contains(x.ScheduleLineId) && x.Capture.WeekId == admissionWeekId &&
                x.Capture.Area == area && x.Capture.Status == ProductionDailyCaptureStatus.Active)
            .GroupBy(x => x.ScheduleLineId).Select(x => new { Id = x.Key, Quantity = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Quantity, token) : new Dictionary<Guid, decimal>();
        var allocations = new List<ProductionDailyAllocationPreview>();
        var legacyUsed = !explicitWeek ? await db.ProductionDailyCaptureAllocations.AsNoTracking()
            .Where(x => lineIds.Contains(x.ScheduleLineId) && !x.Capture.Week.ExplicitCarryover && x.Capture.Area == area && x.Capture.Status == ProductionDailyCaptureStatus.Active)
            .GroupBy(x => x.ScheduleLineId).Select(x => new { Id = x.Key, Quantity = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Quantity, token) : new Dictionary<Guid, decimal>();
        var reserved = await db.ProductionWeekOpenings.AsNoTracking()
            .Where(x => lineIds.Contains(x.SourceLineId) && x.Area == area &&
                (explicitWeek ? x.SourceWeekId == admissionWeekId : x.SourceWeekId == db.ProductionScheduleLines.Where(l => l.Id == x.SourceLineId).Select(l => l.WeekId).First()))
            .GroupBy(x => x.SourceLineId).Select(x => new { Id = x.Key, Quantity = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Quantity, token);
        foreach (var line in lines)
        {
            if (line.WorkOrder!.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress)) continue;
            var stage = line.WorkOrder!.Stages.SingleOrDefault(x => x.SourceStageId == stageId);
            if (stage is null || !Applies(line, area)) continue;
            var pending = line.Quantity - used.GetValueOrDefault(line.Id);
            if (explicitWeek)
                pending = Math.Min(pending, (line.WeekId == admissionWeekId ? line.Quantity : admissions.GetValueOrDefault(line.Id)) - weeklyUsed.GetValueOrDefault(line.Id) - reserved.GetValueOrDefault(line.Id));
            else pending = Math.Min(pending, line.Quantity - legacyUsed.GetValueOrDefault(line.Id) - reserved.GetValueOrDefault(line.Id));
            if (pending <= 0) continue;
            var batch = line.WorkOrder.Batches.SingleOrDefault();
            if (batch is null) continue;
            var available = AvailableInput(line.WorkOrder, batch, stage);
            if (available <= 0) continue;
            var quantity = Math.Min(Math.Min(pending, remaining), available);
            allocations.Add(new(line.Id, line.WorkOrder.Id, stage.Id, batch.Id, line.PlannedDate,
                line.WorkOrder.Number, quantity, References(line)));
            remaining -= quantity;
            if (remaining <= 0) break;
        }
        return allocations;
    }

    private async Task<ProductionDailyCommandResult> ReconcileAsync(Guid productId, Guid actorId, string pin, CancellationToken token)
    {
        // Area order ensures downstream records can use newly materialized upstream results, even when entered first.
        var captures = await db.ProductionDailyCaptures.Include(x => x.Allocations)
            .Where(x => x.ProductId == productId && x.IsFlexible && x.Status == ProductionDailyCaptureStatus.Active)
            .ToListAsync(token);
        var throughWeek = captures.Max(x => x.EffectiveDate);
        // Area is stored as text in PostgreSQL; sort the enum in memory, never lexicographically in SQL.
        foreach (var capture in captures.OrderBy(x => x.Area).ThenBy(x => x.EffectiveDate).ThenBy(x => x.RecordedAt).ThenBy(x => x.Id))
        {
            var remaining = capture.Quantity - capture.Allocations.Sum(x => x.Quantity);
            if (remaining <= 0) continue;
            var explicitWeek = await db.ProductionScheduleWeeks.AsNoTracking().AnyAsync(x => x.Id == capture.WeekId && x.ExplicitCarryover, token);
            if (explicitWeek && (await new ProductionWeekOpeningService(db).RevalidateAsync(capture.WeekId, token)).Count > 0)
                continue; // Keep residual captures until their own admitted sources are reviewed.
            var allocations = await AllocateAsync(productId, capture.StageId, capture.Area, remaining,
                explicitWeek ? capture.EffectiveDate : throughWeek, token, capture.WeekId);
            var result = await ApplyAllocationsAsync(capture, allocations, actorId, pin, token);
            if (!result.Success) return result;
        }
        return new(ProductionDailyCommandStatus.Success);
    }

    private async Task<ProductionDailyCommandResult> ApplyAllocationsAsync(ProductionDailyCapture capture,
        IReadOnlyList<ProductionDailyAllocationPreview> allocations, Guid actorId, string pin, CancellationToken token)
    {
        var traceability = new ProductionTraceabilityService(db, pins,
            new ProductionMaterialService(db, pins, movements, timeProvider), timeProvider);
        var engine = new ProductionService(db, pins, movements, timeProvider);
            foreach (var allocation in allocations)
            {
                var allocatedBefore = capture.Allocations.Where(x => x.WorkOrderStageId == allocation.WorkOrderStageId).Sum(x => x.Quantity);
                var allocationOperation = Derive(capture.OperationId, allocation.ScheduleLineId, allocation.WorkOrderStageId,
                    "reconcile:" + allocatedBefore.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var order = await LoadOrderAsync(allocation.WorkOrderId, token);
                if (order is null) return NotReady($"{allocation.OrderNumber}: la orden ya no existe.");
                var materials = await SelectMaterialsAsync(order, allocation.WorkOrderStageId, allocation.Quantity, token);
                if (materials.Errors.Count > 0)
                    return new(ProductionDailyCommandStatus.ValidationFailed, Errors: materials.Errors);
                var processOperation = Derive(allocationOperation, allocation.ScheduleLineId, allocation.WorkOrderStageId, "result");
                var result = await traceability.RecordResultAsync(new RecordBatchResultCommand(
                    processOperation, order.Id, allocation.BatchId, allocation.WorkOrderStageId, capture.ShiftId,
                    false, allocation.Quantity, allocation.Quantity, 0, 0, materials.Selections,
                    order.Version, null, pin), token);
                if (!result.Success || result.Id is not Guid resultId)
                    return TraceFailure(allocation.OrderNumber, result);
                order = await LoadOrderAsync(order.Id, token) ?? throw new InvalidOperationException();
                Guid? deliveryOperation = null;
                Guid? receiveOperation = null;
                var stages = order.Stages.OrderBy(x => x.Sequence).ToArray();
                var source = stages.Single(x => x.Id == allocation.WorkOrderStageId);
                var target = stages.SingleOrDefault(x => x.Sequence == source.Sequence + 1);
                if (target is not null)
                {
                    deliveryOperation = Derive(allocationOperation, allocation.ScheduleLineId, allocation.WorkOrderStageId, "delivery");
                    var delivered = await engine.DeliverAsync(new ProductionHandoffCommand(
                        deliveryOperation.Value, order.Id, source.Id, target.Id, allocation.Quantity,
                        pin, "Entrega automática de captura diaria", allocation.BatchId,
                        ExpectedVersion: order.Version), token);
                    if (delivered.Status != ProductionCommandStatus.Success)
                        return EngineFailure(allocation.OrderNumber, delivered);
                    var deliveryEvent = await db.ProductionEvents.AsNoTracking()
                        .SingleAsync(x => x.OperationId == deliveryOperation.Value, token);
                    order = await LoadOrderAsync(order.Id, token) ?? throw new InvalidOperationException();
                    receiveOperation = Derive(allocationOperation, allocation.ScheduleLineId, allocation.WorkOrderStageId, "receive");
                    var received = await engine.ReceiveAsync(new ProductionHandoffCommand(
                        receiveOperation.Value, order.Id, source.Id, target.Id, allocation.Quantity,
                        pin, "Recepción automática de captura diaria", allocation.BatchId,
                        deliveryEvent.Id, order.Version), token);
                    if (received.Status != ProductionCommandStatus.Success)
                        return EngineFailure(allocation.OrderNumber, received);
                }
                var captureAllocation = new ProductionDailyCaptureAllocation
                {
                    ReconciledAt = timeProvider.GetUtcNow(),
                    ReconciledByUserId = actorId,
                    ScheduleLineId = allocation.ScheduleLineId,
                    WorkOrderId = allocation.WorkOrderId,
                    WorkOrderStageId = allocation.WorkOrderStageId,
                    BatchResultId = resultId,
                    Quantity = allocation.Quantity,
                    ProcessOperationId = processOperation,
                    DeliveryOperationId = deliveryOperation,
                    ReceiveOperationId = receiveOperation
                };
                capture.Allocations.Add(captureAllocation);
                // The capture was already saved by the result above; a preset key would otherwise be tracked as an update.
                db.Entry(captureAllocation).State = EntityState.Added;
                await db.SaveChangesAsync(token);
            }
        return new(ProductionDailyCommandStatus.Success);
    }
}
