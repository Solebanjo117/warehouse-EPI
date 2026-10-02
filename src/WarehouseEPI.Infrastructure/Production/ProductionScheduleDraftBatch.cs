using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyScheduleService
{
    public Task<ProductionDailyCommandResult> SaveDraftChangesAsync(
        SaveProductionScheduleDraftCommand command, CancellationToken token = default) =>
        SaveWorkspaceChangesCoreAsync(command, false, "", token);

    public Task<ProductionDailyCommandResult> SaveWorkspaceChangesAsync(
        SaveProductionScheduleDraftCommand command, string adminPin = "", CancellationToken token = default) =>
        SaveWorkspaceChangesCoreAsync(command, true, adminPin, token);

    private async Task<ProductionDailyCommandResult> SaveWorkspaceChangesCoreAsync(
        SaveProductionScheduleDraftCommand command, bool allowOpen, string adminPin, CancellationToken token)
    {
        if (!await IsAdminAsync(command.ActorUserId, token))
            return Invalid("La programación requiere un usuario ADMIN autenticado.");
        var openingChanges = command.Openings ?? [];
        var initialChanges = command.InitialBalances ?? [];
        var initialService = new ProductionInitialBalanceService(db);
        if (command.Changes.Count + openingChanges.Count + initialChanges.Count + (command.SkuTotals?.Count ?? 0) is < 1)
            return Invalid("Agrega al menos un cambio antes de confirmar.");
        // Preserve the pre-review contract's hash for retries prepared before this extension.
        var fingerprint = command.SkuTotals is not null ? Fingerprint(command) : command.InitialBalances is null && string.IsNullOrEmpty(command.Reason) && string.IsNullOrEmpty(command.ReviewedFingerprint)
            ? Fingerprint(new { command.OperationId, command.WeekId, command.ExpectedWeekVersion,
                command.Changes, command.ActorUserId, command.Openings })
            : command.InitialBalances is null
                ? Fingerprint(new { command.OperationId, command.WeekId, command.ExpectedWeekVersion,
                    command.Changes, command.ActorUserId, command.Openings, command.Reason, command.ReviewedFingerprint })
                : Fingerprint(command);
        var prior = await db.ProductionScheduleRevisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null)
            return prior.RequestFingerprint == fingerprint
                ? new(ProductionDailyCommandStatus.Success, command.WeekId)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);

        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        try
        {
            var week = await db.ProductionScheduleWeeks.Include(x => x.Lines)
                .SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
            if (week is null) return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.NotFound), token);
            if (week.Status != ProductionScheduleWeekStatus.Draft && !(allowOpen && week.Status == ProductionScheduleWeekStatus.Open))
                return await CancelAbortAsync(transaction, Invalid("Reabre la semana antes de modificarla."), token);
            if (week.Version != command.ExpectedWeekVersion)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ConcurrencyConflict), token);
            if (command.SkuTotals is { Count: > 0 } || week.Status == ProductionScheduleWeekStatus.Open && (openingChanges.Count > 0 || initialChanges.Count > 0))
            {
                var review = await PreviewWorkspaceChangesAsync(command, token);
                if (!review.CanConfirm)
                    return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ValidationFailed, Errors: review.Errors), token);
                if (string.IsNullOrEmpty(command.ReviewedFingerprint) || command.ReviewedFingerprint != review.Fingerprint)
                    return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ConcurrencyConflict,
                        Errors: ["La preparación o los pendientes cambiaron. Revisa nuevamente antes de guardar."]), token);
            }
            var expanded = await ExpandSkuTotalsAsync(week, command, token);
            if (expanded.Errors.Count > 0)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ValidationFailed, Errors: expanded.Errors), token);
            command = command with { Changes = expanded.Changes };
            var initialErrors = await initialService.ValidateAsync(week.Id, initialChanges, token);
            if (initialErrors.Count > 0)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ValidationFailed, Errors: initialErrors), token);
            var initialBefore = initialChanges.Count > 0 ? await initialService.GetAsync(week.Id, token) : [];
            var beforeInitials = initialChanges.Select(change => {
                var before = initialBefore.SingleOrDefault(x => x.ProductId == change.ProductId && x.Area == change.Area);
                return new { change.ProductId, change.Area, Quantity = before?.Quantity ?? 0, Version = before?.Version ?? 0 };
            }).ToArray();
            var openingService = new ProductionWeekOpeningService(db);
            IReadOnlyList<string> openingErrors = openingChanges.Count > 0 ? await openingService.ValidateAsync(week.Id, openingChanges, token) : [];
            if (openingChanges.Count > 0 && openingErrors.Count > 0)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ValidationFailed, Errors: openingErrors), token);
            var beforeOpenings = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.WeekId == week.Id).ToListAsync(token);
            if (week.ExplicitCarryover && command.InitialBalances is null)
            {
                var allOpeningChanges = beforeOpenings.Where(x => x.Quantity > 0 && !openingChanges.Any(c => c.SourceWeekId == x.SourceWeekId && c.SourceLineId == x.SourceLineId && c.Area == x.Area))
                    .Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Quantity, x.SourceFingerprint)).Concat(openingChanges).ToArray();
                var allErrors = await openingService.ValidateAsync(week.Id, allOpeningChanges, token);
                if (allErrors.Count > 0) return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ValidationFailed, Errors: allErrors), token);
            }
            var touched = new HashSet<Guid>();
            for (var index = 0; index < command.Changes.Count; index++)
            {
                var change = command.Changes[index];
                if (change.Kind is not ("add" or "edit" or "remove") ||
                    (change.Kind == "add" && change.LineId.HasValue) ||
                    (change.Kind != "add" && (!change.LineId.HasValue || !change.ExpectedLineVersion.HasValue)) ||
                    (change.Kind != "remove" && change.Line is null) ||
                    (change.Kind == "remove" && change.Line is not null))
                    return await CancelAbortAsync(transaction, Invalid($"Cambio {index + 1}: acción no válida."), token);
                if (change.LineId is Guid id)
                {
                    var current = week.Lines.SingleOrDefault(x => x.Id == id);
                    if (!touched.Add(id) || current is null || current.IsCancelled || current.IsCarryover || current.IsExtra ||
                        current.Version != change.ExpectedLineVersion)
                        return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ConcurrencyConflict), token);
                }
            }
            var productIds = command.Changes.Where(x => x.Line is not null)
                .Select(x => x.Line!.ProductId).Distinct().ToArray();
            var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit)
                .Where(x => productIds.Contains(x.Id) && x.IsActive)
                .ToDictionaryAsync(x => x.Id, token);
            var removals = command.Changes.Where(x => x.Kind == "remove").Select(x => x.LineId!.Value).ToArray();
            var eligibility = await GetDeletionEligibilityAsync(week.Id, removals, token);
            if (week.Status == ProductionScheduleWeekStatus.Open &&
                (command.SkuTotals is { Count: > 0 } || (openingChanges.Count > 0 || initialChanges.Count > 0) || command.Changes.Any(x => x.Kind == "add") || eligibility.Values.Any(x => x.RequiresPin)))
            {
                var actor = await pins.AuthenticateAsync(adminPin, token);
                if (actor?.Role.Code != "ADMIN" || actor.Id != command.ActorUserId)
                    return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.InvalidPin,
                        Errors: ["El NIP ADMIN no corresponde a la sesión actual."]), token);
            }
            for (var index = 0; index < command.Changes.Count; index++)
            {
                var change = command.Changes[index];
                if (change.Kind == "remove")
                {
                    if (!eligibility.TryGetValue(change.LineId!.Value, out var allowed) || !allowed.Allowed)
                        return await CancelAbortAsync(transaction, Invalid($"Cambio {index + 1}: {allowed?.Reason ?? "No se puede quitar este renglón."}"), token);
                    continue;
                }
                var line = change.Line!;
                if (line.PlannedDate < week.WeekStart || line.PlannedDate > week.WeekEnd ||
                    line.Quantity <= 0 || line.Quantity > 99999999999999.9999m ||
                    decimal.Round(line.Quantity, 4) != line.Quantity ||
                    !products.TryGetValue(line.ProductId, out var product) ||
                    !product.BaseUnit.AllowsDecimals && decimal.Truncate(line.Quantity) != line.Quantity ||
                    new[] { line.OrderReference1, line.OrderReference2, line.OrderReference3, line.OriginalType }
                        .Any(x => x?.Length > 120) ||
                    new[] { line.Notes, line.OriginalAnnotation1, line.OriginalAnnotation2 }
                        .Any(x => x?.Length > 500))
                    return await CancelAbortAsync(transaction, Invalid($"Cambio {index + 1}: revisa SKU, día, cantidad y detalles."), token);
            }
            for (var index = 0; index < command.Changes.Count; index++)
            {
                var change = command.Changes[index];
                var rowKey = new byte[16];
                BitConverter.TryWriteBytes(rowKey, index);
                var rowOperation = Derive(command.OperationId, new Guid(rowKey), "draft-change");
                ProductionDailyCommandResult result;
                if (change.Kind == "remove")
                    result = await CancelLineAsync(new(rowOperation, command.WeekId, change.LineId!.Value,
                        week.Version, change.ExpectedLineVersion!.Value, command.ActorUserId, adminPin), token);
                else
                {
                    var line = change.Line!;
                    result = await SaveLineAsync(new(rowOperation, command.WeekId, change.LineId,
                        week.Version, change.ExpectedLineVersion, line.PlannedDate, line.ProductId, line.Quantity,
                        line.OrderReference1, line.OrderReference2, line.OrderReference3, line.Notes,
                        command.ActorUserId, adminPin, line.OriginalType, line.OriginalAnnotation1,
                        line.OriginalAnnotation2, line.OriginalAnnotation1Kind, line.OriginalAnnotation2Kind), token);
                }
                if (!result.Success)
                {
                    var errors = result.Errors is { Count: > 0 }
                        ? result.Errors.Select(x => $"Cambio {index + 1}: {x}").ToArray()
                        : [$"Cambio {index + 1}: no se pudo guardar."];
                    return await CancelAbortAsync(transaction, result with { Errors = errors }, token);
                }
            }
            await openingService.ApplyAsync(week.Id, openingChanges, token);
            await initialService.ApplyAsync(week.Id, initialChanges, token);
            week.Version += (uint)(openingChanges.Count + initialChanges.Count);
            db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fingerprint, week.Id,
                null, week.Status == ProductionScheduleWeekStatus.Draft ? "draft-batch-changed" : "open-batch-changed", JsonSerializer.Serialize(new { Openings = beforeOpenings, InitialBalances = beforeInitials }), JsonSerializer.Serialize(new { Count = command.Changes.Count + openingChanges.Count + initialChanges.Count, Openings = openingChanges, InitialBalances = initialChanges, command.Reason, command.ReviewedFingerprint }),
                command.ActorUserId));
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, week.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException ||
            exception.GetBaseException() is PostgresException { SqlState: "40001" or "40P01" })
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        }
    }
}
