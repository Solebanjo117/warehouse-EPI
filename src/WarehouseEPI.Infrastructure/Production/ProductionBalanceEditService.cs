using System.Data;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionBalanceCell(Guid ProductId, ProductionDailyArea Area, int Shift, decimal Observed, decimal Requested);
public sealed record ProductionBalanceEditCommand(Guid OperationId, Guid WeekId, DateOnly Date,
    IReadOnlyList<ProductionBalanceCell> Cells, string? Reason = null, string ReviewedFingerprint = "", string Pin = "",
    IReadOnlyList<ProductionBalancePlanChange>? PlanChanges = null, Guid? AdminActorId = null,
    ProductionWeeklyFilter? Filter = null, IReadOnlyList<ProductionBalanceNewPlan>? NewPlans = null);
public sealed record ProductionBalanceNewPlan(Guid OperationId, Guid ProductId, decimal Requested, uint ExpectedWeekVersion);
public sealed record ProductionBalanceNewPlanReview(ProductionBalanceNewPlan Change, string Sku, IReadOnlyList<string> Errors, uint CurrentWeekVersion);
public sealed record ProductionBalancePlanChange(Guid LineId, decimal Observed, decimal Requested,
    uint ExpectedLineVersion, uint ExpectedWeekVersion);
public sealed record ProductionBalancePlanReview(ProductionBalancePlanChange Change, string Sku, decimal Current,
    IReadOnlyList<string> Errors, uint CurrentLineVersion = 0, uint CurrentWeekVersion = 0);
public sealed record ProductionBalanceCellReview(ProductionBalanceCell Cell, string Sku, decimal Current,
    IReadOnlyList<Guid> Reversals, IReadOnlyList<ProductionBalanceAddition> Additions, IReadOnlyList<string> Errors);
public sealed record ProductionBalanceEditPreview(bool CanConfirm, bool RequiresAdmin, string Fingerprint,
    IReadOnlyList<ProductionBalanceCellReview> Cells, ProductionDailySummary? Balance, IReadOnlyList<string> Errors,
    IReadOnlyList<ProductionBalancePlanReview>? Plans = null, ProductionWeekClose? WeekClose = null,
    bool RequiresReason = false, IReadOnlyList<ProductionBalanceNewPlanReview>? NewPlans = null);

public sealed partial class ProductionDailyCaptureService
{
    public async Task<ProductionBalanceEditPreview> PreviewBalanceEditAsync(ProductionBalanceEditCommand command, CancellationToken token = default)
    {
        var errors = new List<string>();
        var reviews = new List<ProductionBalanceCellReview>();
        var planChanges = command.PlanChanges ?? [];
        var newPlans = command.NewPlans ?? [];
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
        if (week is null || week.Status != ProductionScheduleWeekStatus.Open || command.Date < week.WeekStart || command.Date > week.WeekEnd)
            errors.Add("La fecha debe pertenecer a una semana abierta.");
        if (command.OperationId == Guid.Empty || command.Cells.Count + planChanges.Count + newPlans.Count is < 1 or > 100 ||
            command.Cells.Select(x => (x.ProductId, x.Area, x.Shift)).Distinct().Count() != command.Cells.Count ||
            planChanges.Select(x => x.LineId).Distinct().Count() != planChanges.Count ||
            newPlans.Any(x => x.OperationId == Guid.Empty) || newPlans.Select(x => x.OperationId).Distinct().Count() != newPlans.Count ||
            newPlans.Select(x => x.ProductId).Distinct().Count() != newPlans.Count)
            errors.Add("Selecciona entre 1 y 100 celdas sin repetir.");
        if (command.Reason?.Length > 500) errors.Add("Usa como máximo 500 caracteres en el motivo.");
        if (errors.Count > 0) return new(false, false, "", reviews, null, errors);
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var plans = await ReviewPlanChangesAsync(command, token);
        var newReviews = await ReviewNewPlansAsync(command, week!, token);
        errors.AddRange(newReviews.SelectMany(x => x.Errors.Select(e => $"{x.Sku}: {e}")));
        errors.AddRange(plans.SelectMany(x => x.Errors.Select(e => $"{x.Sku}: {e}")));
        var planIds = planChanges.Select(x => x.LineId).ToArray();
        var planProducts = await db.ProductionScheduleLines.AsNoTracking().Where(x => planIds.Contains(x.Id)).Select(x => x.ProductId).ToArrayAsync(token);
        var ids = command.Cells.Select(x => x.ProductId).Concat(planProducts).Concat(newPlans.Select(x => x.ProductId)).Distinct().ToArray();
        var state = await db.ProductionDailyCaptures.AsNoTracking().Include(x => x.Allocations)
            .Where(x => ids.Contains(x.ProductId)).ToListAsync(token);
        var baseBalance = (await new ProductionDailyBalanceService(db).GetDailySummaryAsync(command.WeekId, new(command.Date), token))!;
        foreach (var cell in command.Cells.OrderBy(x => x.Shift).ThenBy(x => x.Area).ThenBy(x => x.ProductId))
        {
            var cellErrors = new List<string>();
            var shift = cell.Shift == 1 ? config.Shift1Id : cell.Shift == 2 ? config.Shift2Id : null;
            var currentCaptures = state.Where(x => x.EffectiveDate == command.Date && x.Area == cell.Area && x.ShiftId == shift &&
                x.ProductId == cell.ProductId && x.Status == ProductionDailyCaptureStatus.Active)
                .OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id).ToArray();
            var current = currentCaptures.Sum(x => x.Quantity);
            var sku = await db.Products.AsNoTracking().Where(x => x.Id == cell.ProductId).Select(x => x.Sku).SingleOrDefaultAsync(token) ?? cell.ProductId.ToString();
            var row = baseBalance.Products.SingleOrDefault(x => x.ProductId == cell.ProductId);
            var area = row is null ? null : cell.Area switch { ProductionDailyArea.Cutting => row.Cutting, ProductionDailyArea.Sewing => row.Sewing, _ => row.ReadyToPack };
            if (!Enum.IsDefined(cell.Area) || area?.Applies != true) cellErrors.Add("El proceso no aplica a este producto.");
            if (shift is null) cellErrors.Add("Selecciona T1 o T2 configurado y activo.");
            if (cell.Requested < 0 || cell.Requested > 99999999999999.9999m || decimal.Round(cell.Requested, 4) != cell.Requested)
                cellErrors.Add("Indica un total no negativo con hasta cuatro decimales.");
            if (cell.Observed != current) cellErrors.Add("El total cambió. Revisa los valores actualizados.");
            // Reuse product, date, unit, shift and configuration validation, including for reductions to zero.
            if (shift is Guid shiftId && cell.Requested >= 0)
            {
                var validation = await PreviewAsync(new(command.Date, cell.Area, shiftId, cell.ProductId, cell.Requested == 0 ? 1 : cell.Requested), token);
                cellErrors.AddRange(validation.Blockers);
            }
            var reversals = new List<Guid>();
            var additions = new List<ProductionBalanceAddition>();
            if (shift is Guid actualShift && cellErrors.Count == 0)
            {
                if (cell.Requested > current)
                    additions.Add(new(Derive(command.OperationId, cell.ProductId, actualShift, $"edit-add-{cell.Area}"), cell.ProductId, cell.Area, actualShift, cell.Requested - current));
                else
                {
                    var reduce = current - cell.Requested;
                    foreach (var capture in currentCaptures)
                    {
                        if (reduce <= 0) break;
                        var blocker = await BalanceReversalBlockerAsync(capture, state.Where(x => reversals.Contains(x.Id)).SelectMany(x => x.Allocations), token);
                        if (blocker is not null) { cellErrors.Add(blocker); break; }
                        reversals.Add(capture.Id);
                        var removed = Math.Min(reduce, capture.Quantity);
                        if (removed < capture.Quantity)
                            additions.Add(new(Derive(command.OperationId, capture.Id, actualShift, "edit-replacement"), cell.ProductId, cell.Area,
                                actualShift, capture.Quantity - removed, capture.Notes));
                        reduce -= removed;
                    }
                }
            }
            reviews.Add(new(cell, sku, current, reversals, additions, cellErrors.Distinct().ToArray()));
        }
        var orderIds = state.SelectMany(x => x.Allocations).Select(x => x.WorkOrderId).Distinct().ToArray();
        var versions = await db.ProductionWorkOrders.AsNoTracking().Where(x => orderIds.Contains(x.Id) || ids.Contains(x.ProductId))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.Status }).ToListAsync(token);
        var fingerprint = Fingerprint(new { command.WeekId, command.Date, week!.Version, Cells = reviews.Select(x => new { x.Cell, x.Current, x.Reversals, x.Additions }),
            Plans = plans, NewPlans = newReviews, command.AdminActorId,
            State = state.OrderBy(x => x.Id).Select(x => new { x.Id, x.Status, Allocations = x.Allocations.OrderBy(a => a.Id).Select(a => new { a.Id, a.Quantity }) }), versions });
        var requiresReason = reviews.Any(x => x.Cell.Requested < x.Current);
        var requiresAdmin = requiresReason || planChanges.Count > 0 || newPlans.Count > 0;
        errors.AddRange(reviews.SelectMany(x => x.Errors.Select(e => $"{x.Sku}: {e}")));
        if (errors.Count == 0)
            errors.AddRange(await PlanActivityErrorsAsync(command, reviews.SelectMany(x => x.Reversals).ToArray(), token));
        var scenario = new ProductionBalanceScenario(command.Date, reviews.SelectMany(x => x.Reversals).ToArray(),
            reviews.SelectMany(x => x.Additions).ToArray(), planChanges.ToDictionary(x => x.LineId, x => x.Requested), newPlans);
        var balanceService = new ProductionDailyBalanceService(db);
        var projected = errors.Count == 0 ? await balanceService.GetDailySummaryAsync(command.WeekId, new(command.Date), scenario, token) : null;
        var close = errors.Count == 0 ? await balanceService.GetWeekCloseAsync(command.WeekId,
            command.Filter ?? new(command.Date), scenario, token) : null;
        return new(errors.Count == 0, requiresAdmin, fingerprint, reviews, projected, errors, plans, close, requiresReason, newReviews);
    }

    private async Task<string?> BalanceReversalBlockerAsync(ProductionDailyCapture capture, IEnumerable<ProductionDailyCaptureAllocation> earlierReversals, CancellationToken token)
    {
        foreach (var allocation in capture.Allocations)
        {
            var order = await LoadOrderAsync(allocation.WorkOrderId, token);
            if (order is null || order.Status is ProductionWorkOrderStatus.Closed or ProductionWorkOrderStatus.PrincipalClosed)
                return "Reabre la orden antes de corregir resultados.";
            var result = await db.ProductionBatchResults.AsNoTracking().SingleOrDefaultAsync(x => x.Id == allocation.BatchResultId, token);
            if (result is null) return "Una asignación ya no está disponible para reverso.";
            var ownOperations = capture.Allocations.Concat(earlierReversals).SelectMany(a => new Guid?[] { a.ProcessOperationId, a.DeliveryOperationId, a.ReceiveOperationId }).ToHashSet();
            var dependent = order.Events.FirstOrDefault(e => e.BatchId == result.BatchId && e.RecordedAt >= result.RecordedAt &&
                e.Type != ProductionEventType.ResultReversed && !ownOperations.Contains(e.OperationId) &&
                !order.Events.Any(r => r.Type == ProductionEventType.ResultReversed && r.RelatedEventId == e.Id));
            if (dependent is not null) return $"La captura {capture.Id} tiene una operación posterior dependiente ({dependent.Id}). Corrígela desde el historial antes de reducir este total.";
        }
        return null;
    }

    public async Task<ProductionDailyCommandResult> ConfirmBalanceEditAsync(ProductionBalanceEditCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code is not ("ADMIN" or "OPERATOR")) return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["NIP inválido."]);
        if ((command.PlanChanges?.Count > 0 || command.NewPlans?.Count > 0) && (user.Role.Code != "ADMIN" || command.AdminActorId != user.Id))
            return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["El NIP ADMIN no corresponde a la sesión actual."]);
        var fingerprint = command.NewPlans?.Count > 0
            ? Fingerprint(new { command.WeekId, command.Date, Cells = command.Cells.OrderBy(x => x.Shift).ThenBy(x => x.Area).ThenBy(x => x.ProductId),
                Plans = (command.PlanChanges ?? []).OrderBy(x => x.LineId), NewPlans = command.NewPlans.OrderBy(x => x.OperationId), command.AdminActorId, Reason = command.Reason?.Trim(), user.Id })
            : command.PlanChanges?.Count > 0
            ? Fingerprint(new { command.WeekId, command.Date, Cells = command.Cells.OrderBy(x => x.Shift).ThenBy(x => x.Area).ThenBy(x => x.ProductId),
                Plans = command.PlanChanges.OrderBy(x => x.LineId), command.AdminActorId, Reason = command.Reason?.Trim(), user.Id })
            : Fingerprint(new { command.WeekId, command.Date, Cells = command.Cells.OrderBy(x => x.Shift).ThenBy(x => x.Area).ThenBy(x => x.ProductId), Reason = command.Reason?.Trim(), user.Id });
        async Task<ProductionDailyCommandResult?> Prior()
        {
            var prior = await db.Set<ProductionBalanceEdit>().AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            return prior is null ? null : prior.RequestFingerprint == fingerprint ? new(ProductionDailyCommandStatus.Success, prior.Id) : new(ProductionDailyCommandStatus.IdempotencyConflict);
        }
        if (await Prior() is { } existing) return existing;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        try
        {
            var preview = await PreviewBalanceEditAsync(command, token);
            if (!preview.CanConfirm) return new(ProductionDailyCommandStatus.ValidationFailed, Errors: preview.Errors);
            if (preview.RequiresAdmin && user.Role.Code != "ADMIN") return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["NIP ADMIN inválido."]);
            if (preview.RequiresReason && string.IsNullOrWhiteSpace(command.Reason)) return NotReady("Indica el motivo del reverso.");
            if (preview.Fingerprint != command.ReviewedFingerprint) return new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: ["El balance cambió. Revisa nuevamente los cambios."]);
            var edit = new ProductionBalanceEdit { OperationId = command.OperationId, RequestFingerprint = fingerprint, WeekId = command.WeekId,
                EffectiveDate = command.Date, Reason = command.Reason?.Trim(), ResponsibleUserId = user.Id, RecordedAt = timeProvider.GetUtcNow() };
            foreach (var cell in preview.Cells)
            {
                foreach (var captureId in cell.Reversals)
                {
                    var reversed = await ReverseAsync(new(Derive(command.OperationId, captureId, Guid.Empty, "edit-reverse"), captureId, command.Reason!, command.Pin), token);
                    if (!reversed.Success) return await AbortBalanceEditAsync(transaction, reversed, token);
                    edit.Items.Add(new() { ProductId = cell.Cell.ProductId, Area = cell.Cell.Area,
                        ShiftId = (await db.ProductionDailyCaptures.AsNoTracking().SingleAsync(x => x.Id == captureId, token)).ShiftId,
                        PreviousTotal = cell.Current, RequestedTotal = cell.Cell.Requested, ReversedCaptureId = captureId });
                }
            }
            var planResult = await ApplyPlanChangesAsync(command, user.Id, token);
            if (!planResult.Success) return await AbortBalanceEditAsync(transaction, planResult, token);
            foreach (var cell in preview.Cells)
                foreach (var addition in cell.Additions)
                {
                    var created = await ConfirmAsync(new(addition.Id, command.Date, addition.Area, addition.ShiftId, addition.ProductId, addition.Quantity, addition.Notes, command.Pin), token);
                    if (!created.Success) return await AbortBalanceEditAsync(transaction, created, token);
                    edit.Items.Add(new() { ProductId = addition.ProductId, Area = addition.Area, ShiftId = addition.ShiftId, PreviousTotal = cell.Current,
                        RequestedTotal = cell.Cell.Requested, CreatedCaptureId = created.Id, ReversedCaptureId = cell.Reversals.LastOrDefault() is var id && id != Guid.Empty ? id : null });
                }
            var actual = await new ProductionDailyBalanceService(db).GetDailySummaryAsync(command.WeekId, new(command.Date), token);
            var editedProducts = preview.Balance!.Products.Select(x => x.ProductId).ToHashSet();
            var actualRows = actual!.Products.Where(x => editedProducts.Contains(x.ProductId)).OrderBy(x => x.ProductId).ToArray();
            var reviewedRows = preview.Balance!.Products.Where(x => editedProducts.Contains(x.ProductId)).OrderBy(x => x.ProductId).ToArray();
            // Compare decimal values, not their JSON scale (PostgreSQL returns numeric(18,4)).
            if (actualRows.Length != reviewedRows.Length || actualRows.Zip(reviewedRows).Any(pair =>
                pair.First.ProductId != pair.Second.ProductId || pair.First.Planned != pair.Second.Planned ||
                pair.First.Cutting != pair.Second.Cutting || pair.First.Sewing != pair.Second.Sewing ||
                pair.First.ReadyToPack != pair.Second.ReadyToPack || !pair.First.Intentions.SequenceEqual(pair.Second.Intentions)))
                return await AbortBalanceEditAsync(transaction, NotReady("La proyección cambió al aplicar la conciliación. No se guardaron los cambios."), token);
            db.Set<ProductionBalanceEdit>().Add(edit);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, edit.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException || exception.GetBaseException() is Npgsql.PostgresException { SqlState: "40001" or "40P01" })
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return await Prior() ?? new(ProductionDailyCommandStatus.ConcurrencyConflict);
        }
    }

    private async Task<ProductionDailyCommandResult> AbortBalanceEditAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, ProductionDailyCommandResult result,
        CancellationToken token)
    {
        if (transaction is not null) await transaction.RollbackAsync(token);
        db.ChangeTracker.Clear();
        return result;
    }
}
