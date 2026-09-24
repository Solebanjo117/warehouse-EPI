using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionBalancePlanLine(Guid LineId, int Sequence, Guid ProductId, string Sku,
    string Unit, decimal Quantity, string? Reference1, string? Reference2, string? Reference3,
    uint LineVersion, uint WeekVersion);

public sealed partial class ProductionDailyCaptureService
{
    public async Task<IReadOnlyList<ProductionBalancePlanLine>> GetBalancePlanLinesAsync(Guid weekId,
        DateOnly date, Guid? productId, Guid actorId, CancellationToken token = default)
    {
        if (!await db.Users.AnyAsync(x => x.Id == actorId && x.IsActive && x.Role.Code == "ADMIN", token) ||
            date > await clock.GetDateAsync(timeProvider.GetUtcNow(), token)) return [];
        return await db.ProductionScheduleLines.AsNoTracking()
            .Where(x => x.WeekId == weekId && (productId == null || x.ProductId == productId) && x.PlannedDate == date &&
                x.Week.Status == ProductionScheduleWeekStatus.Open && !x.IsCancelled && !x.IsCarryover && !x.IsExtra)
            .OrderBy(x => x.Sequence).ThenBy(x => x.Id)
            .Select(x => new ProductionBalancePlanLine(x.Id, x.Sequence, x.ProductId, x.Product.Sku,
                x.Product.BaseUnit.Code, x.Quantity, x.OrderReference1, x.OrderReference2, x.OrderReference3,
                x.Version, x.Week.Version)).ToArrayAsync(token);
    }

    private async Task<IReadOnlyList<ProductionBalancePlanReview>> ReviewPlanChangesAsync(
        ProductionBalanceEditCommand command, CancellationToken token)
    {
        var changes = command.PlanChanges ?? [];
        if (changes.Count == 0) return [];
        var admin = command.AdminActorId.HasValue && await db.Users.AnyAsync(x =>
            x.Id == command.AdminActorId && x.IsActive && x.Role.Code == "ADMIN", token);
        var ids = changes.Select(x => x.LineId).ToArray();
        var lines = await db.ProductionScheduleLines.AsNoTracking().Include(x => x.Week)
            .Include(x => x.Product).ThenInclude(x => x.BaseUnit).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        return changes.Select(change =>
        {
            var errors = new List<string>();
            lines.TryGetValue(change.LineId, out var line);
            if (!admin) errors.Add("La programación requiere un usuario ADMIN autenticado.");
            if (line is null || line.WeekId != command.WeekId || line.PlannedDate != command.Date ||
                line.IsCancelled || line.IsCarryover || line.IsExtra)
                errors.Add("Selecciona un renglón de programación nueva del día.");
            if (line is not null)
            {
                if (line.Version != change.ExpectedLineVersion || line.Week.Version != change.ExpectedWeekVersion || line.Quantity != change.Observed)
                    errors.Add("El programa cambió. Revisa nuevamente las cantidades.");
                if (!line.Product.IsActive) errors.Add("Selecciona un SKU activo del catálogo.");
                if (!line.Product.BaseUnit.AllowsDecimals && decimal.Truncate(change.Requested) != change.Requested)
                    errors.Add("La unidad del SKU no permite decimales.");
            }
            if (change.Requested <= 0 || change.Requested > 99999999999999.9999m || decimal.Round(change.Requested, 4) != change.Requested)
                errors.Add("La cantidad debe ser positiva y admitir hasta cuatro decimales.");
            return new ProductionBalancePlanReview(change, line?.Product.Sku ?? "—", line?.Quantity ?? 0, errors,
                line?.Version ?? 0, line?.Week.Version ?? 0);
        }).ToArray();
    }

    private async Task<IReadOnlyList<ProductionBalanceNewPlanReview>> ReviewNewPlansAsync(
        ProductionBalanceEditCommand command, ProductionScheduleWeek week, CancellationToken token)
    {
        var changes = command.NewPlans ?? [];
        if (changes.Count == 0) return [];
        var admin = command.AdminActorId.HasValue && await db.Users.AnyAsync(x =>
            x.Id == command.AdminActorId && x.IsActive && x.Role.Code == "ADMIN", token);
        var visible = await new ProductionDailyBalanceService(db).GetDailySummaryAsync(command.WeekId, new(command.Date), token);
        var ids = changes.Select(x => x.ProductId).ToArray();
        var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        var existing = await db.ProductionScheduleLines.AsNoTracking().Where(x => x.WeekId == command.WeekId &&
            x.PlannedDate == command.Date && !x.IsCancelled && !x.IsExtra && !x.IsCarryover && ids.Contains(x.ProductId))
            .Select(x => x.ProductId).ToArrayAsync(token);
        return changes.Select(change =>
        {
            var errors = new List<string>();
            products.TryGetValue(change.ProductId, out var product);
            if (!admin) errors.Add("La programación requiere un usuario ADMIN autenticado.");
            if (week.Version != change.ExpectedWeekVersion || existing.Contains(change.ProductId))
                errors.Add("El programa cambió. Revisa nuevamente las cantidades.");
            if (visible?.Products.Any(x => x.ProductId == change.ProductId) != true)
                errors.Add("Selecciona un producto visible en el balance del día.");
            if (product?.IsActive != true) errors.Add("Selecciona un SKU activo del catálogo.");
            if (product is not null && !product.BaseUnit.AllowsDecimals && decimal.Truncate(change.Requested) != change.Requested)
                errors.Add("La unidad del SKU no permite decimales.");
            if (change.Requested <= 0 || change.Requested > 99999999999999.9999m || decimal.Round(change.Requested, 4) != change.Requested)
                errors.Add("La cantidad debe ser positiva y admitir hasta cuatro decimales.");
            return new ProductionBalanceNewPlanReview(change, product?.Sku ?? "—", errors, week.Version);
        }).ToArray();
    }

    private async Task<IReadOnlyList<string>> PlanActivityErrorsAsync(ProductionBalanceEditCommand command,
        IReadOnlyCollection<Guid> reversals, CancellationToken token)
    {
        var errors = new List<string>();
        var changes = command.PlanChanges ?? [];
        if (changes.Count == 0) return errors;
        var ids = changes.Select(x => x.LineId).ToArray();
        var lines = await db.ProductionScheduleLines.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.WorkOrderId, x.Product.Sku }).ToListAsync(token);
        var operations = await db.ProductionDailyCaptureAllocations.AsNoTracking()
            .Where(x => reversals.Contains(x.CaptureId)).Select(x => x.ProcessOperationId).ToArrayAsync(token);
        foreach (var line in lines.Where(x => x.WorkOrderId.HasValue))
        {
            var events = await db.ProductionEvents.AsNoTracking().Where(x => x.WorkOrderId == line.WorkOrderId).ToListAsync(token);
            var reversed = events.Where(x => x.Type == ProductionEventType.ResultReversed && x.RelatedEventId.HasValue)
                .Select(x => x.RelatedEventId!.Value).ToHashSet();
            var processed = events.Where(x => !reversed.Contains(x.Id) && !operations.Contains(x.OperationId) &&
                    x.Type is ProductionEventType.Processed or ProductionEventType.Reworked)
                .GroupBy(x => x.WorkOrderStageId).Select(x => x.Sum(e => e.GoodQuantity)).DefaultIfEmpty(0).Max();
            if (changes.Single(x => x.LineId == line.Id).Requested < processed)
                errors.Add($"{line.Sku}: La cantidad no puede ser menor que lo ya procesado ({processed:0.####}).");
        }
        return errors;
    }

    private async Task<ProductionDailyCommandResult> ApplyPlanChangesAsync(ProductionBalanceEditCommand command,
        Guid actorId, CancellationToken token)
    {
        var service = new ProductionDailyScheduleService(db, pins, movements, timeProvider);
        foreach (var change in (command.PlanChanges ?? []).OrderBy(x => x.LineId))
        {
            // Read current versions after our preceding changes. All submitted versions were checked together in preview.
            var line = await db.ProductionScheduleLines.Include(x => x.Week).SingleAsync(x => x.Id == change.LineId, token);
            var result = await service.SaveLineAsync(new(Derive(command.OperationId, line.Id, Guid.Empty, "balance-plan"),
                command.WeekId, line.Id, line.Week.Version, line.Version, line.PlannedDate, line.ProductId,
                change.Requested, line.OrderReference1, line.OrderReference2, line.OrderReference3, line.Notes,
                actorId, command.Pin, line.OriginalType, line.OriginalAnnotation1, line.OriginalAnnotation2,
                line.OriginalAnnotation1Kind, line.OriginalAnnotation2Kind), token);
            if (!result.Success) return result;
        }
        foreach (var change in (command.NewPlans ?? []).OrderBy(x => x.OperationId))
        {
            var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == command.WeekId, token);
            var result = await service.SaveLineAsync(new(Derive(command.OperationId, change.OperationId, Guid.Empty, "balance-new-plan"),
                command.WeekId, null, week.Version, null, command.Date, change.ProductId, change.Requested,
                null, null, null, null, actorId, command.Pin), token);
            if (!result.Success) return result;
        }
        return new(ProductionDailyCommandStatus.Success);
    }
}
