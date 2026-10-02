using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyScheduleService
{
    // Review only reads persisted state. Confirmation repeats these checks inside its transaction.
    public async Task<ProductionScheduleWorkspaceReview> PreviewWorkspaceChangesAsync(
        SaveProductionScheduleDraftCommand command, CancellationToken token = default)
    {
        if (!await IsAdminAsync(command.ActorUserId, token))
            return new(false, "", ["La programación requiere un usuario ADMIN autenticado."]);
        var week = await db.ProductionScheduleWeeks.AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
        if (week is null || week.Status == ProductionScheduleWeekStatus.Closed || week.Version != command.ExpectedWeekVersion)
            return new(false, "", ["La semana cambió. Actualiza y revisa de nuevo."]);
        var request = command;
        var expanded = await ExpandSkuTotalsAsync(week, command, token);
        if (expanded.Errors.Count > 0) return new(false, "", expanded.Errors);
        command = command with { Changes = expanded.Changes };
        var changes = command.Openings ?? [];
        var initials = command.InitialBalances ?? [];
        if (command.OperationId == Guid.Empty || command.Changes.Count + changes.Count + initials.Count + (command.SkuTotals?.Count ?? 0) is < 1)
            return new(false, "", ["Agrega al menos un cambio antes de confirmar."]);
        if (week.Status == ProductionScheduleWeekStatus.Open && (changes.Count > 0 || initials.Count > 0) &&
            (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 500))
            return new(false, "", ["Indica un motivo de hasta 500 caracteres para cambiar el arrastre de la semana abierta."]);
        var errors = new List<string>();
        errors.AddRange(await new ProductionInitialBalanceService(db).ValidateAsync(week.Id, initials, token));
        var touched = new HashSet<Guid>();
        var productIds = command.Changes.Where(x => x.Line is not null).Select(x => x.Line!.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit)
            .Where(x => productIds.Contains(x.Id) && x.IsActive).ToDictionaryAsync(x => x.Id, token);
        foreach (var change in command.Changes)
        {
            if (change.Kind is not ("add" or "edit" or "remove") ||
                (change.Kind == "add" && change.LineId.HasValue) ||
                (change.Kind != "add" && (!change.LineId.HasValue || !change.ExpectedLineVersion.HasValue)) ||
                (change.Kind != "remove" && change.Line is null) || (change.Kind == "remove" && change.Line is not null))
            { errors.Add("La preparación contiene una acción no válida."); continue; }
            ProductionScheduleLine? current = null;
            if (change.LineId is Guid id)
            {
                current = week.Lines.SingleOrDefault(x => x.Id == id);
                if (!touched.Add(id) || current is null || current.IsCancelled || current.IsExtra || current.IsCarryover || current.Version != change.ExpectedLineVersion)
                { errors.Add("Un renglón cambió. Actualiza y revisa de nuevo."); continue; }
            }
            if (change.Line is not { } line) continue;
            if (!products.TryGetValue(line.ProductId, out var product) ||
                !PreparedLineValid(line, week.WeekStart, week.WeekEnd, product.BaseUnit.AllowsDecimals))
                errors.Add("Revisa SKU, día, cantidad y detalles de los productos preparados.");
            if (current?.WorkOrderId is Guid orderId && line.Quantity < await EffectiveGoodAsync(orderId, token))
                errors.Add("La cantidad no puede ser menor que lo ya procesado.");
        }
        var removals = command.Changes.Where(x => x.Kind == "remove" && x.LineId.HasValue).Select(x => x.LineId!.Value).ToArray();
        var eligibility = await GetDeletionEligibilityAsync(week.Id, removals, token);
        errors.AddRange(eligibility.Values.Where(x => !x.Allowed).Select(x => x.Reason ?? "No se puede quitar este renglón."));
        var saved = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.WeekId == week.Id && x.Quantity > 0)
            .OrderBy(x => x.Id).ToListAsync(token);
        if (week.ExplicitCarryover && command.InitialBalances is null)
        {
            var all = saved.Where(x => !changes.Any(c => c.SourceWeekId == x.SourceWeekId && c.SourceLineId == x.SourceLineId && c.Area == x.Area))
                .Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Quantity, x.SourceFingerprint)).Concat(changes).ToArray();
            errors.AddRange(await new ProductionWeekOpeningService(db).ValidateAsync(week.Id, all, token));
        }
        else if (changes.Count > 0) errors.Add("Esta semana usa el modelo anterior. Crea una semana nueva para traer arrastre inicial.");
        if (errors.Count > 0) return new(false, "", errors.Distinct().ToArray());
        var initialState = await db.ProductionInitialBalances.AsNoTracking().Where(x => x.WeekId == week.Id).OrderBy(x => x.Id).ToListAsync(token);
        var captures = await db.ProductionDailyCaptures.AsNoTracking().Where(x => x.WeekId == week.Id)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Status, x.Quantity, x.Area, x.EffectiveDate, x.ShiftId }).ToListAsync(token);
        var options = changes.Count > 0 ? await new ProductionWeekOpeningService(db).OptionsAsync(week.Id, token) : [];
        var sourceState = options.Where(o => changes.Any(c => c.SourceWeekId == o.SourceWeekId && c.SourceLineId == o.SourceLineId && c.Area == o.Area))
            .OrderBy(x => x.SourceWeekId).ThenBy(x => x.SourceLineId).ThenBy(x => x.Area)
            .Select(x => new { x.SourceWeekId, x.SourceLineId, x.Area, x.Fingerprint, x.Selected });
        var fingerprint = Fingerprint(new { Request = request with { ReviewedFingerprint = "" }, captures,
            Openings = saved.Select(x => new { x.Id, x.Quantity, x.Version, x.SourceFingerprint }), Sources = sourceState, InitialBalances = initialState });
        return new(true, request.SkuTotals is null ? fingerprint : Fingerprint(new { Baseline = fingerprint, Expanded = expanded.Changes }), []);
    }
}
