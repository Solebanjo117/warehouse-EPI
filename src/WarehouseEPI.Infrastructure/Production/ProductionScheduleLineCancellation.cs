using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyScheduleService
{
    private const string CancellationReason = "Renglón eliminado del programa semanal.";

    public async Task<IReadOnlyDictionary<Guid, ProductionScheduleLineDeletion>> GetDeletionEligibilityAsync(
        Guid weekId, IReadOnlyCollection<Guid> lineIds, CancellationToken token = default)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weekId, token);
        if (week is null || lineIds.Count == 0) return new Dictionary<Guid, ProductionScheduleLineDeletion>();
        var lines = await db.ProductionScheduleLines.AsNoTracking()
            .Where(x => x.WeekId == weekId && lineIds.Contains(x.Id)).ToArrayAsync(token);
        var ids = lines.Select(x => x.Id).ToArray();
        var forwarded = (await db.ProductionWeekOpenings.AsNoTracking().Where(x => ids.Contains(x.SourceLineId) && x.Quantity > 0)
            .Select(x => x.SourceLineId).Distinct().ToArrayAsync(token)).ToHashSet();
        var products = lines.Select(x => x.ProductId).Distinct().ToArray();
        var orders = lines.Where(x => x.WorkOrderId.HasValue).Select(x => x.WorkOrderId!.Value).Distinct().ToArray();
        var allocated = (await db.ProductionDailyCaptureAllocations.AsNoTracking()
            .Where(x => ids.Contains(x.ScheduleLineId)).Select(x => x.ScheduleLineId).Distinct().ToArrayAsync(token)).ToHashSet();
        var unallocatedProducts = (await db.ProductionDailyCaptures.AsNoTracking()
            .Where(x => x.WeekId == weekId && products.Contains(x.ProductId) && !x.Allocations.Any())
            .Select(x => x.ProductId).Distinct().ToArrayAsync(token)).ToHashSet();
        var orderStatus = await db.ProductionWorkOrders.AsNoTracking().Where(x => orders.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Status, token);
        var activity = (await db.ProductionEvents.AsNoTracking().Where(x => orders.Contains(x.WorkOrderId) &&
            (x.Type == ProductionEventType.Processed || x.Type == ProductionEventType.Reworked ||
             x.Type == ProductionEventType.Delivered || x.Type == ProductionEventType.Received ||
             x.Type == ProductionEventType.WarehouseReceived))
            .Select(x => x.WorkOrderId).Distinct().ToArrayAsync(token)).ToHashSet();
        activity.UnionWith(await db.ProductionBatchResults.AsNoTracking()
            .Where(x => orders.Contains(x.Batch.WorkOrderId))
            .Select(x => x.Batch.WorkOrderId).Distinct().ToArrayAsync(token));
        var issued = (await db.ProductionMaterialIssueLinks.AsNoTracking()
            .Where(x => orders.Contains(x.WorkOrderId) && x.Quantity > x.CancelledQuantity)
            .Select(x => x.WorkOrderId).Distinct().ToArrayAsync(token)).ToHashSet();
        var materialOperations = (await db.ProductionMaterialOperations.AsNoTracking()
            .Where(x => orders.Contains(x.WorkOrderId) && x.Type != ProductionMaterialOperationType.Reversal &&
                !db.ProductionMaterialOperations.Any(r => r.ReversesOperationId == x.Id))
            .Select(x => x.WorkOrderId).Distinct().ToArrayAsync(token)).ToHashSet();
        var result = new Dictionary<Guid, ProductionScheduleLineDeletion>();
        foreach (var line in lines)
        {
            ProductionScheduleLineDeletion No(string reason) => new(line.Id, false, false, reason);
            result[line.Id] = week.Status == ProductionScheduleWeekStatus.Closed
                ? No("Reabre la semana antes de modificarla.")
                : line.IsCancelled ? No("Este renglón ya fue eliminado.")
                : forwarded.Contains(line.Id) ? No("Este renglón está comprometido en arrastres posteriores. Corrige esos arrastres primero.")
                : line.IsCarryover || line.IsExtra ? No("Solo se pueden eliminar renglones de programación nueva.")
                : allocated.Contains(line.Id) ? No("Este renglón tiene capturas registradas, incluso si fueron revertidas.")
                : unallocatedProducts.Contains(line.ProductId) ? No("Hay capturas del mismo SKU sin asignación a un renglón; no se puede eliminar con seguridad.")
                : week.Status == ProductionScheduleWeekStatus.Draft ? new(line.Id, true, false, null)
                : line.WorkOrderId is not Guid orderId ? No("La línea abierta no tiene una orden vinculada. Revisa su historial.")
                : !orderStatus.TryGetValue(orderId, out var status) || status is ProductionWorkOrderStatus.Cancelled or ProductionWorkOrderStatus.Closed or ProductionWorkOrderStatus.PrincipalClosed
                    ? No("La orden ya no admite cancelación desde el programa.")
                : activity.Contains(orderId) ? No("La orden tiene producción registrada; corrígela desde Producción avanzada.")
                : new(line.Id, true, issued.Contains(orderId) || materialOperations.Contains(orderId), null);
        }
        return result;
    }

    public async Task<ProductionDailyCommandResult> CancelLineAsync(
        CancelProductionScheduleLineCommand command, CancellationToken token = default)
    {
        if (!await IsAdminAsync(command.ActorUserId, token))
            return Invalid("La programación requiere un usuario ADMIN autenticado.");
        var fingerprint = Fingerprint(command with { AdminPin = "" });
        var prior = await db.ProductionScheduleRevisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null)
            return prior.RequestFingerprint == fingerprint
                ? new(ProductionDailyCommandStatus.Success, command.LineId)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);

        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        try
        {
            var week = await db.ProductionScheduleWeeks.SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
            var line = await db.ProductionScheduleLines.SingleOrDefaultAsync(x => x.Id == command.LineId && x.WeekId == command.WeekId, token);
            if (week is null || line is null) return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.NotFound), token);
            if (week.Version != command.ExpectedWeekVersion || line.Version != command.ExpectedLineVersion)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ConcurrencyConflict), token);
            var eligibility = await DeletionEligibilityAsync(week, line, token);
            if (!eligibility.Allowed)
                return await CancelAbortAsync(transaction, Invalid(eligibility.Reason ?? "No se puede eliminar este renglón."), token);
            if (eligibility.RequiresPin)
            {
                var pinUser = await pins.AuthenticateAsync(command.AdminPin, token);
                if (pinUser?.Role.Code != "ADMIN" || pinUser.Id != command.ActorUserId)
                    return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.InvalidPin,
                        Errors: ["El NIP ADMIN no corresponde a la sesión actual."]), token);
            }

            var before = JsonSerializer.Serialize(LineSnapshot(line));
            if (week.Status == ProductionScheduleWeekStatus.Open && line.WorkOrderId is Guid orderId)
            {
                var reversal = await ReverseLineMaterialsAsync(orderId, command, token);
                if (reversal is not null) return await CancelAbortAsync(transaction, reversal, token);
                var cancellation = await CancelLineOrderAsync(orderId, command, token);
                if (cancellation is not null) return await CancelAbortAsync(transaction, cancellation, token);
            }
            line.IsCancelled = true;
            line.Version++;
            week.Version++;
            db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fingerprint, week.Id, line.Id,
                "line-cancelled", before, JsonSerializer.Serialize(LineSnapshot(line)), command.ActorUserId));
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, line.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException ||
            exception.GetBaseException() is PostgresException { SqlState: "40001" or "40P01" })
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        }
    }

    private async Task<ProductionScheduleLineDeletion> DeletionEligibilityAsync(
        ProductionScheduleWeek week, ProductionScheduleLine line, CancellationToken token)
    {
        ProductionScheduleLineDeletion No(string reason) => new(line.Id, false, false, reason);
        if (week.Status == ProductionScheduleWeekStatus.Closed) return No("Reabre la semana antes de modificarla.");
        if (line.IsCancelled) return No("Este renglón ya fue eliminado.");
        if (line.IsCarryover || line.IsExtra) return No("Solo se pueden eliminar renglones de programación nueva.");
        if (await db.ProductionDailyCaptureAllocations.AsNoTracking().AnyAsync(x => x.ScheduleLineId == line.Id, token))
            return No("Este renglón tiene capturas registradas, incluso si fueron revertidas.");
        if (await db.ProductionDailyCaptures.AsNoTracking().AnyAsync(x => x.WeekId == week.Id &&
            x.ProductId == line.ProductId && !x.Allocations.Any(), token))
            return No("Hay capturas del mismo SKU sin asignación a un renglón; no se puede eliminar con seguridad.");
        if (week.Status == ProductionScheduleWeekStatus.Draft)
            return new(line.Id, true, false, null);
        if (line.WorkOrderId is not Guid orderId)
            return No("La línea abierta no tiene una orden vinculada. Revisa su historial.");
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId, token);
        if (order is null || order.Status is ProductionWorkOrderStatus.Cancelled or ProductionWorkOrderStatus.Closed or ProductionWorkOrderStatus.PrincipalClosed)
            return No("La orden ya no admite cancelación desde el programa.");
        if (await db.ProductionEvents.AsNoTracking().AnyAsync(x => x.WorkOrderId == orderId &&
            (x.Type == ProductionEventType.Processed || x.Type == ProductionEventType.Reworked ||
             x.Type == ProductionEventType.Delivered || x.Type == ProductionEventType.Received ||
             x.Type == ProductionEventType.WarehouseReceived), token))
            return No("La orden tiene producción registrada; corrígela desde Producción avanzada.");
        if (await db.ProductionBatchResults.AsNoTracking().AnyAsync(x => x.Batch.WorkOrderId == orderId, token))
            return No("La orden tiene resultados de proceso registrados; corrígela desde Producción avanzada.");
        var requiresPin = await db.ProductionMaterialIssueLinks.AsNoTracking()
            .AnyAsync(x => x.WorkOrderId == orderId && x.Quantity > x.CancelledQuantity, token);
        requiresPin = requiresPin || await db.ProductionMaterialOperations.AsNoTracking()
            .AnyAsync(x => x.WorkOrderId == orderId && x.Type != ProductionMaterialOperationType.Reversal &&
                !db.ProductionMaterialOperations.Any(r => r.ReversesOperationId == x.Id), token);
        return new(line.Id, true, requiresPin, null);
    }

    private async Task<ProductionDailyCommandResult?> ReverseLineMaterialsAsync(
        Guid orderId, CancelProductionScheduleLineCommand command, CancellationToken token)
    {
        var materials = new ProductionMaterialService(db, pins, movements, timeProvider);
        var operations = await db.ProductionMaterialOperations.AsNoTracking()
            .Where(x => x.WorkOrderId == orderId && x.Type != ProductionMaterialOperationType.Reversal &&
                !db.ProductionMaterialOperations.Any(r => r.ReversesOperationId == x.Id))
            .OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id).Select(x => x.Id).ToArrayAsync(token);
        foreach (var operationId in operations)
        {
            var version = await db.ProductionWorkOrders.AsNoTracking().Where(x => x.Id == orderId)
                .Select(x => x.Version).SingleAsync(token);
            var result = await materials.ReverseAsync(Derive(command.OperationId, operationId, "cancel-material"),
                operationId, version, command.AdminPin, CancellationReason, token);
            if (result.Status != ProductionMaterialStatus.Success)
                return Invalid(result.Errors?.FirstOrDefault() ?? "No fue posible revertir una operación de material.");
        }

        var issues = await db.ProductionMaterialIssueLinks.AsNoTracking()
            .Where(x => x.WorkOrderId == orderId && x.Quantity > x.CancelledQuantity)
            .Select(x => new { x.Id, x.SupplyRequestLineId, x.InventoryMovementLineId,
                x.Source, x.Quantity, x.CancelledQuantity }).ToArrayAsync(token);
        var correction = new InventoryCorrectionService(db, pins, movements, timeProvider);
        var movementLineIds = issues.Where(x => x.InventoryMovementLineId.HasValue)
            .Select(x => x.InventoryMovementLineId!.Value).Distinct().ToArray();
        var movementIds = await db.InventoryMovementLines.AsNoTracking()
            .Where(x => movementLineIds.Contains(x.Id)).Select(x => x.MovementId).Distinct().ToArrayAsync(token);
        foreach (var movementId in movementIds.Order())
        {
            var result = await correction.ConfirmAsync(new(Derive(command.OperationId, movementId, "cancel-delivery"),
                movementId, command.ActorUserId, command.AdminPin, CancellationReason), token);
            if (result.Status != InventoryCorrectionStatus.Success)
                return Invalid(result.ValidationErrors.FirstOrDefault() ?? "No fue posible revertir el surtimiento de material.");
        }

        var assignments = new ProductionSupplyPreparationService(db, pins, movements, timeProvider);
        foreach (var issue in issues.Where(x => !x.InventoryMovementLineId.HasValue && x.Source == ProductionMaterialSupplySource.WipAssignment))
        {
            if (issue.SupplyRequestLineId is not Guid supplyLineId)
                return Invalid("La asignación WIP no tiene solicitud vinculada; corrígela desde Surtimientos.");
            var version = await db.ProductionSupplyRequestLines.AsNoTracking().Where(x => x.Id == supplyLineId)
                .Select(x => x.SupplyRequest.Version).SingleAsync(token);
            var result = await assignments.CancelWipAssignmentAsync(new(
                Derive(command.OperationId, issue.Id, "cancel-wip"), supplyLineId, issue.Id, version,
                issue.Quantity - issue.CancelledQuantity, command.AdminPin, CancellationReason), token);
            if (result.Status != ProductionSupplyCommandStatus.Success)
                return Invalid(result.Errors?.FirstOrDefault() ?? "No fue posible anular la asignación WIP.");
        }
        if (await db.ProductionMaterialIssueLinks.AsNoTracking()
            .AnyAsync(x => x.WorkOrderId == orderId && x.Quantity > x.CancelledQuantity, token))
            return Invalid("La orden conserva material surtido. Revisa sus movimientos antes de eliminarla.");
        return null;
    }

    private async Task<ProductionDailyCommandResult?> CancelLineOrderAsync(
        Guid orderId, CancelProductionScheduleLineCommand command, CancellationToken token)
    {
        var order = await db.ProductionWorkOrders.Include(x => x.SupplyRequests).ThenInclude(x => x.Lines)
            .ThenInclude(x => x.Reservations)
            .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.Preparations)
            .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.IssueLinks)
            .SingleAsync(x => x.Id == orderId, token);
        if (order.SupplyRequests.SelectMany(x => x.Lines).SelectMany(x => x.IssueLinks)
            .Any(x => x.Quantity > x.CancelledQuantity))
            return Invalid("La orden conserva material surtido.");
        var now = timeProvider.GetUtcNow();
        foreach (var request in order.SupplyRequests)
        {
            foreach (var line in request.Lines)
            {
                if (line.ReopenedQuantity > 0)
                    return Invalid("La solicitud conserva material por reponer; revisa el reverso antes de eliminar.");
                var pending = line.RequiredQuantity - line.CancelledQuantity;
                if (pending > 0)
                {
                    line.CancelledQuantity += pending;
                    foreach (var reservation in line.Reservations.Where(x => x.Quantity > x.ReleasedQuantity))
                        reservation.ReleasedQuantity = reservation.Quantity;
                    db.ProductionSupplyEvents.Add(new ProductionSupplyEvent
                    {
                        OperationId = Derive(command.OperationId, line.Id, "cancel-supply"),
                        RequestFingerprint = Fingerprint(new { line.Id, pending, CancellationReason }),
                        SupplyRequestId = request.Id, SupplyRequestLineId = line.Id,
                        Type = ProductionSupplyEventType.QuantityCancelled,
                        ResponsibleUserId = command.ActorUserId, Quantity = pending,
                        Reason = CancellationReason, RecordedAt = now
                    });
                }
                foreach (var preparation in line.Preparations.Where(x => x.Status == ProductionSupplyPreparationStatus.Open))
                {
                    preparation.Status = ProductionSupplyPreparationStatus.Discarded;
                    preparation.Version++;
                    preparation.UpdatedAt = now;
                }
            }
            ProductionSupplyService.UpdateStatus(request);
            request.Version++;
        }
        order.Status = ProductionWorkOrderStatus.Cancelled;
        order.Version++;
        db.ProductionEvents.Add(new ProductionEvent
        {
            OperationId = Derive(command.OperationId, order.Id, "cancel-order"),
            RequestFingerprint = Fingerprint(new { order.Id, CancellationReason }),
            WorkOrderId = order.Id, Type = ProductionEventType.Cancelled,
            ResponsibleUserId = command.ActorUserId, Reason = CancellationReason, RecordedAt = now
        });
        return null;
    }

    private async Task<ProductionDailyCommandResult> CancelAbortAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        ProductionDailyCommandResult result, CancellationToken token)
    {
        if (transaction is not null) await transaction.RollbackAsync(token);
        db.ChangeTracker.Clear();
        return result;
    }
}
