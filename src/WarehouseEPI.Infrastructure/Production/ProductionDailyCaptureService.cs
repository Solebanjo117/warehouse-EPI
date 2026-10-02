using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyCaptureService(
    WarehouseDbContext db,
    UserPinService pins,
    InventoryMovementService movements,
    WarehouseClock clock,
    TimeProvider timeProvider)
{
    // Retain the constructor dependency for existing callers; planning is no longer limited to today.
    private readonly WarehouseClock clock = clock;

    public async Task<ProductionDailyCapturePreview> PreviewAsync(
        PreviewProductionDailyCaptureCommand command, CancellationToken token = default) =>
        await PreviewAsync(command, await ReadCaptureContextAsync(command.EffectiveDate, [command.ProductId], token), token);

    private async Task<ProductionDailyCapturePreview> PreviewAsync(
        PreviewProductionDailyCaptureCommand command, CaptureReadContext context, CancellationToken token)
    {
        using var measurement = ProductionBalanceDiagnostics.Source.StartActivity("capture.validation");
        var blockers = new List<string>();
        if (!Enum.IsDefined(command.Area)) blockers.Add("El área seleccionada no está configurada.");
        if (command.Quantity <= 0 || command.Quantity > 99999999999999.9999m || decimal.Round(command.Quantity, 4) != command.Quantity)
            blockers.Add("Indica una cantidad positiva con hasta cuatro decimales.");
        var config = context.Configuration;
        var stageId = Stage(config, command.Area);
        if (stageId is not null && !context.ActiveStages.Contains(stageId.Value))
            blockers.Add("El área seleccionada no está configurada.");
        if (stageId is null) blockers.Add("El área seleccionada no está configurada.");
        if (!context.ActiveShifts.Contains(command.ShiftId))
            blockers.Add("Selecciona T1 o T2 configurado y activo.");
        var product = context.Products.GetValueOrDefault(command.ProductId);
        if (product?.IsActive != true) product = null;
        if (product is null) blockers.Add("Selecciona un SKU activo del catálogo.");
        else if (!product.BaseUnit.AllowsDecimals && decimal.Truncate(command.Quantity) != command.Quantity)
            blockers.Add("La unidad del SKU no admite decimales.");
        var effectiveWeek = context.Week;
        if (effectiveWeek is null) blockers.Add("La fecha debe pertenecer a una semana abierta.");
        else if (effectiveWeek.ExplicitCarryover)
            blockers.AddRange(context.OpeningErrors);
        if (effectiveWeek is not null && await new ProductionWeekOpeningService(db)
            .CaptureCommitmentErrorAsync(effectiveWeek.Id, command.ProductId, command.Area, command.Quantity, token) is string commitmentError)
            blockers.Add(commitmentError);
        if (blockers.Count > 0 || stageId is null)
            return new(false, effectiveWeek?.Id, stageId, product?.Sku, command.Quantity, [], blockers);
        var allocations = await AllocateAsync(command.ProductId, stageId.Value, command.Area, command.Quantity, effectiveWeek!.WeekStart, token, effectiveWeek.Id);
        foreach (var allocation in allocations)
        {
            var order = await LoadOrderAsync(allocation.WorkOrderId, token);
            if (order is null) { blockers.Add($"{allocation.OrderNumber}: la orden ya no está disponible."); continue; }
            var materials = await SelectMaterialsAsync(order, allocation.WorkOrderStageId, allocation.Quantity, token);
            blockers.AddRange(materials.Errors.Select(x => $"{allocation.OrderNumber}: {x}"));
        }
        var availabilityKey = (command.EffectiveDate, command.Area, command.ProductId);
        if (!context.Availability.TryGetValue(availabilityKey, out var availability))
        {
            var selected = context.Products.Keys.ToArray();
            var availableProducts = (await GetAvailabilityAsync(command.EffectiveDate, command.Area, false, selected, token))
                .ToDictionary(x => x.ProductId);
            foreach (var productId in selected)
                context.Availability[(command.EffectiveDate, command.Area, productId)] = availableProducts.GetValueOrDefault(productId);
            availability = context.Availability.GetValueOrDefault(availabilityKey);
        }
        var available = availability?.Available ?? 0;
        var pending = availability?.Pending ?? 0;
        if (availability is null)
        {
            if (!context.Pending.TryGetValue(effectiveWeek.Id, out var balance))
            {
                balance = await new ProductionDailyBalanceService(db).GetAsync(effectiveWeek.Id, false, false, token,
                    selectedProducts: context.Products.Keys.ToArray());
                context.Pending.Add(effectiveWeek.Id, balance);
            }
            var row = balance!.Rows.SingleOrDefault(x => x.ProductId == command.ProductId && x.Date == command.EffectiveDate);
            pending = row is null ? 0 : (command.Area switch
            {
                ProductionDailyArea.Cutting => row.Cutting,
                ProductionDailyArea.Sewing => row.Sewing,
                _ => row.ReadyToPack
            }).SignedPending;
        }
        var allocatedLineIds = allocations.Select(x => x.ScheduleLineId).ToArray();
        var extraLineIds = await db.ProductionScheduleLines.AsNoTracking().Where(x => allocatedLineIds.Contains(x.Id) && x.IsExtra)
            .Select(x => x.Id).ToListAsync(token);
        var assignedExtras = allocations.Where(x => extraLineIds.Contains(x.ScheduleLineId)).Sum(x => x.Quantity);
        var state = await db.ProductionDailyCaptures.AsNoTracking().Where(x => x.ProductId == command.ProductId)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Status, Allocated = x.Allocations.Sum(a => a.Quantity) }).ToListAsync(token);
        return new(blockers.Count == 0, effectiveWeek!.Id, stageId, product?.Sku, command.Quantity,
            allocations, blockers.Distinct().ToArray(), available,
            command.Area == ProductionDailyArea.Cutting ? Math.Max(0, command.Quantity - allocations.Sum(x => x.Quantity)) : assignedExtras,
            command.Area == ProductionDailyArea.Cutting ? 0 : Math.Max(0, command.Quantity - available), Fingerprint(state), pending);
    }

    public Task<ProductionDailyCommandResult> ConfirmAsync(
        ConfirmProductionDailyCaptureCommand command, CancellationToken token = default) => ConfirmAsync(command, false, token);

    private async Task<ProductionDailyCommandResult> ConfirmAsync(
        ConfirmProductionDailyCaptureCommand command, bool validatedBalanceEdit, CancellationToken token)
    {
        User? user;
        using (ProductionBalanceDiagnostics.Source.StartActivity("capture.authentication"))
            user = await pins.AuthenticateAsync(command.Pin, token);
        if (user is null) return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["NIP inválido."]);
        if (!RoleAccess.CanCaptureProduction(user.Role.Code)) return new(ProductionDailyCommandStatus.RoleNotAllowed, Errors: [RoleAccess.ProductionWarning]);
        var fingerprint = Fingerprint(command with { Pin = "" });
        var prior = await db.ProductionDailyCaptures.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null)
            return prior.RequestFingerprint == fingerprint && prior.ResponsibleUserId == user.Id
                ? new(ProductionDailyCommandStatus.Success, prior.Id)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);
        // Only the balance editor can omit the preliminary validation; it has just revalidated
        // the edit within its serializable transaction. Always validate again at the write point.
        Guid? preliminaryWeekId = null, preliminaryStageId = null;
        if (!validatedBalanceEdit)
        {
            var preliminary = await PreviewAsync(new(command.EffectiveDate, command.Area, command.ShiftId,
                command.ProductId, command.Quantity), token);
            if (!preliminary.CanConfirm || preliminary.WeekId is null || preliminary.StageId is null)
                return new(ProductionDailyCommandStatus.ValidationFailed, Errors: preliminary.Blockers);
            preliminaryWeekId = preliminary.WeekId; preliminaryStageId = preliminary.StageId;
        }
        var ownsTransaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token)
            : null;
        try
        {
            var preview = await PreviewAsync(new(command.EffectiveDate, command.Area, command.ShiftId, command.ProductId, command.Quantity), token);
            if (!preview.CanConfirm || preview.WeekId is not Guid weekId || preview.StageId is not Guid stageId)
                return await AbortAsync(transaction, new(ProductionDailyCommandStatus.ValidationFailed, Errors: preview.Blockers), token);
            var capture = new ProductionDailyCapture
            {
                IsFlexible = true,
                OperationId = command.OperationId,
                RequestFingerprint = fingerprint,
                WeekId = preliminaryWeekId ?? weekId,
                EffectiveDate = command.EffectiveDate,
                Area = command.Area,
                StageId = preliminaryStageId ?? stageId,
                ShiftId = command.ShiftId,
                ProductId = command.ProductId,
                Quantity = command.Quantity,
                Notes = Trim(command.Notes, 500),
                ResponsibleUserId = user.Id,
                RecordedAt = timeProvider.GetUtcNow()
            };
            db.ProductionDailyCaptures.Add(capture);
            await db.SaveChangesAsync(token);
            if (command.Area == ProductionDailyArea.Cutting && preview.Extra > 0)
            {
                var extra = await new ProductionDailyScheduleService(db, pins, movements, timeProvider)
                    .CreateExtraAsync(capture, preview.Extra, user.Id, command.Pin, token);
                if (!extra.Success) return await AbortAsync(transaction, extra, token);
            }
            var reconciled = await ReconcileAsync(capture.ProductId, user.Id, command.Pin, token);
            if (!reconciled.Success) return await AbortAsync(transaction, reconciled, token);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, capture.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is null && db.Database.CurrentTransaction is not null) throw;
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(ProductionDailyCommandStatus.ConcurrencyConflict,
                Errors: ["La programación o una orden cambió. Se actualizó la previsualización sin duplicar resultados."]);
        }
        catch (DbUpdateException)
        {
            if (transaction is null && db.Database.CurrentTransaction is not null) throw;
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            prior = await db.ProductionDailyCaptures.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            return prior?.RequestFingerprint == fingerprint && prior.ResponsibleUserId == user.Id
                ? new(ProductionDailyCommandStatus.Success, prior.Id)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);
        }
    }

    public async Task<ProductionDailyCommandResult> ReverseAsync(
        ReverseProductionDailyCaptureCommand command, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(command.Reason)) return NotReady("Indica el motivo del reverso.");
        var user = await pins.AuthenticateAsync(command.AdminPin, token);
        if (user?.Role.Code != "ADMIN") return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["NIP ADMIN inválido."]);
        var ownsTransaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        var capture = await db.ProductionDailyCaptures.Include(x => x.Allocations)
            .SingleOrDefaultAsync(x => x.Id == command.CaptureId, token);
        if (capture is null) return new(ProductionDailyCommandStatus.NotFound);
        var fp = Fingerprint(new { command.CaptureId, Reason = command.Reason.Trim(), user.Id });
        var prior = await db.ProductionEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (capture.Status == ProductionDailyCaptureStatus.Reversed)
            return (capture.ReverseOperationId == command.OperationId && capture.ReverseFingerprint == fp) || prior?.RequestFingerprint == fp
                ? new(ProductionDailyCommandStatus.Success, capture.Id)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);
        try
        {
            var traceability = new ProductionTraceabilityService(db, pins,
                new ProductionMaterialService(db, pins, movements, timeProvider), timeProvider);
            foreach (var allocation in capture.Allocations.OrderByDescending(x => x.ReconciledAt).ThenByDescending(x => x.Id))
            {
                var order = await LoadOrderAsync(allocation.WorkOrderId, token);
                if (order is null || allocation.BatchResultId is not Guid resultId)
                    return await AbortAsync(transaction, NotReady("Una asignación ya no está disponible para reverso."), token);
                await ReverseHandoffsAsync(order, allocation, command.OperationId, command.Reason.Trim(), user.Id, token);
                await db.SaveChangesAsync(token);
                order = await LoadOrderAsync(order.Id, token) ?? throw new InvalidOperationException();
                var reversed = await traceability.ReverseResultAsync(
                    Derive(command.OperationId, allocation.Id, allocation.WorkOrderStageId, "result-reversal"),
                    resultId, order.Version, command.Reason, command.AdminPin, token);
                if (!reversed.Success)
                    return await AbortAsync(transaction, TraceFailure(order.Number, reversed), token);
            }
            if (capture.Area == ProductionDailyArea.Cutting)
            {
                var ids = capture.Allocations.Select(x => x.ScheduleLineId).ToArray();
                var detached = await ProductionScheduleImportService.DetachedLineIdsAsync(db, ids, token);
                foreach (var line in await db.ProductionScheduleLines.Include(x => x.WorkOrder).Where(x => ids.Contains(x.Id) && x.IsExtra).ToListAsync(token))
                {
                    // Retiring a plan never converts its existing order into a disposable capture-created order.
                    if (detached.Contains(line.Id)) continue;
                    line.IsCancelled = true;
                    line.Version++;
                    var order = line.WorkOrder!;
                    order.Status = ProductionWorkOrderStatus.Cancelled;
                    order.Version++;
                    db.ProductionEvents.Add(new ProductionEvent
                    {
                        OperationId = Derive(command.OperationId, line.Id, order.Id, "cancel-extra"),
                        RequestFingerprint = fp, WorkOrderId = order.Id, Type = ProductionEventType.Cancelled,
                        ResponsibleUserId = user.Id, Reason = command.Reason.Trim(), RecordedAt = timeProvider.GetUtcNow()
                    });
                }
            }
            capture.ReverseOperationId = command.OperationId;
            capture.ReverseFingerprint = fp;
            capture.Status = ProductionDailyCaptureStatus.Reversed;
            capture.ReversedByUserId = user.Id;
            capture.ReversedAt = timeProvider.GetUtcNow();
            capture.ReverseReason = Trim(command.Reason, 500);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, capture.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException || exception.GetBaseException() is Npgsql.PostgresException { SqlState: "40001" or "40P01" })
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        }
    }

    private async Task ReverseHandoffsAsync(ProductionWorkOrder order, ProductionDailyCaptureAllocation allocation,
        Guid operationId, string reason, Guid actorId, CancellationToken token)
    {
        foreach (var sourceOperation in new[] { allocation.ReceiveOperationId, allocation.DeliveryOperationId }.Where(x => x.HasValue))
        {
            var original = await db.ProductionEvents.SingleOrDefaultAsync(x => x.OperationId == sourceOperation!.Value, token);
            if (original is null || await db.ProductionEvents.AnyAsync(x => x.Type == ProductionEventType.ResultReversed && x.RelatedEventId == original.Id, token))
                continue;
            var reverseOperation = Derive(operationId, allocation.Id, original.Id, "handoff-reversal");
            db.ProductionEvents.Add(new ProductionEvent
            {
                OperationId = reverseOperation,
                RequestFingerprint = Fingerprint(new { original.Id, reason, actorId }),
                WorkOrderId = order.Id,
                WorkOrderStageId = original.WorkOrderStageId,
                RelatedStageId = original.RelatedStageId,
                BatchId = original.BatchId,
                RelatedEventId = original.Id,
                Type = ProductionEventType.ResultReversed,
                ResponsibleUserId = actorId,
                Quantity = original.Quantity,
                Reason = reason,
                RecordedAt = timeProvider.GetUtcNow()
            });
            order.Version++;
        }
    }

    private async Task<(IReadOnlyList<BatchMaterialInput> Selections, IReadOnlyList<string> Errors)> SelectMaterialsAsync(
        ProductionWorkOrder order, Guid stageId, decimal quantity, CancellationToken token)
    {
        var errors = new List<string>();
        var selections = new List<BatchMaterialInput>();
        var issues = await new ProductionMaterialService(db, pins, movements, timeProvider).GetIssuesAsync(order.Id, token);
        foreach (var plan in order.MaterialPlan.Where(x => x.WorkOrderStageId == stageId))
        {
            var required = decimal.Round(plan.PlannedQuantity * quantity / order.AuthorizedQuantity, 4, MidpointRounding.AwayFromZero);
            var available = issues.Where(x => x.StageId == stageId && x.ProductId == plan.MaterialProductId && x.Pending > 0)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.IssueLinkId).ToArray();
            var remaining = required;
            foreach (var issue in available)
            {
                var take = Math.Min(remaining, issue.Pending);
                if (take > 0) selections.Add(new(issue.IssueLinkId, take));
                remaining -= take;
                if (remaining <= 0) break;
            }
            if (remaining > 0)
                errors.Add($"faltan {remaining:0.####} de {plan.MaterialProduct.Sku} surtido al proceso. Abre Surtimientos.");
        }
        return (selections, errors);
    }

    private Task<ProductionWorkOrder?> LoadOrderAsync(Guid orderId, CancellationToken token) =>
        db.ProductionWorkOrders.Include(x => x.Product).Include(x => x.Unit).Include(x => x.Stages)
            .Include(x => x.Events).Include(x => x.Batches).ThenInclude(x => x.Results)
            .Include(x => x.MaterialPlan).ThenInclude(x => x.MaterialProduct)
            .SingleOrDefaultAsync(x => x.Id == orderId, token);
    private static bool Applies(ProductionScheduleLine line, ProductionDailyArea area) =>
        line.StartArea is null || Rank(area) >= Rank(line.StartArea.Value);
    private static decimal AvailableInput(ProductionWorkOrder order, ProductionBatch batch,
        ProductionWorkOrderStage stage)
    {
        var reversedEventIds = order.Events.Where(x => x.Type == ProductionEventType.ResultReversed && x.RelatedEventId.HasValue)
            .Select(x => x.RelatedEventId!.Value).ToHashSet();
        var reversedOperations = order.Events.Where(x => reversedEventIds.Contains(x.Id)).Select(x => x.OperationId).ToHashSet();
        var input = stage.Sequence == 1 ? batch.AssignedQuantity : order.Events
            .Where(x => !reversedEventIds.Contains(x.Id) && x.BatchId == batch.Id &&
                        x.RelatedStageId == stage.Id && x.Type == ProductionEventType.Received).Sum(x => x.Quantity);
        var processed = batch.Results.Where(x => x.WorkOrderStageId == stage.Id && !x.IsRework &&
                                                  !reversedOperations.Contains(x.OperationId)).Sum(x => x.InputQuantity);
        return Math.Max(0, input - processed);
    }

    private static int Rank(ProductionDailyArea area) => area switch
    {
        ProductionDailyArea.Cutting => 1,
        ProductionDailyArea.Sewing => 2,
        _ => 3
    };
    private static Guid? Stage(ProductionDailyConfiguration config, ProductionDailyArea area) => area switch
    {
        ProductionDailyArea.Cutting => config.CuttingStageId,
        ProductionDailyArea.Sewing => config.SewingStageId,
        _ => config.ReadyToPackStageId
    };
    private static IReadOnlyList<string> References(ProductionScheduleLine line) =>
        new[] { line.OrderReference1, line.OrderReference2, line.OrderReference3 }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
    private static Guid Derive(Guid operationId, Guid first, Guid second, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId:N}:{first:N}:{second:N}:{purpose}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static ProductionDailyCommandResult TraceFailure(string order, ProductionTraceabilityResult result) =>
        new(result.Conflict ? ProductionDailyCommandStatus.ConcurrencyConflict : ProductionDailyCommandStatus.ValidationFailed,
            Errors: result.Errors?.Select(x => $"{order}: {x}").ToArray() ?? [$"{order}: no fue posible registrar el resultado."]);
    private static ProductionDailyCommandResult EngineFailure(string order, ProductionCommandResult result) =>
        new(result.Status == ProductionCommandStatus.ConcurrencyConflict ? ProductionDailyCommandStatus.ConcurrencyConflict :
            result.Status == ProductionCommandStatus.InvalidPin ? ProductionDailyCommandStatus.InvalidPin : ProductionDailyCommandStatus.ValidationFailed,
            Errors: result.Errors?.Select(x => $"{order}: {x}").ToArray() ?? [$"{order}: no fue posible completar el traspaso."]);
    private static ProductionDailyCommandResult NotReady(string error) =>
        new(ProductionDailyCommandStatus.ValidationFailed, Errors: [error]);
    private static async Task<ProductionDailyCommandResult> AbortAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        ProductionDailyCommandResult result, CancellationToken token)
    {
        if (transaction is not null) await transaction.RollbackAsync(token);
        return result;
    }

    private static string? Trim(string? value, int max)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return value?.Length > max ? value[..max] : value;
    }

    private static string Fingerprint<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
