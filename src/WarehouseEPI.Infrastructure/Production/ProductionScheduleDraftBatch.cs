using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyScheduleService
{
    public async Task<ProductionDailyCommandResult> SaveDraftChangesAsync(
        SaveProductionScheduleDraftCommand command, CancellationToken token = default)
    {
        if (!await IsAdminAsync(command.ActorUserId, token))
            return Invalid("La programación requiere un usuario ADMIN autenticado.");
        var openingChanges = command.Openings ?? [];
        if (command.Changes.Count + openingChanges.Count is < 1 or > 100)
            return Invalid("Revisa entre 1 y 100 cambios antes de confirmar.");
        var fingerprint = Fingerprint(command);
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
            if (week.Status != ProductionScheduleWeekStatus.Draft)
                return await CancelAbortAsync(transaction, Invalid("La edición conjunta requiere una semana en borrador."), token);
            if (week.Version != command.ExpectedWeekVersion)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ConcurrencyConflict), token);
            var openingService = new ProductionWeekOpeningService(db);
            var openingErrors = await openingService.ValidateAsync(week.Id, openingChanges, token);
            if (openingChanges.Count > 0 && openingErrors.Count > 0)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ValidationFailed, Errors: openingErrors), token);
            var beforeOpenings = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.WeekId == week.Id).ToListAsync(token);
            if (week.ExplicitCarryover)
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
                        week.Version, change.ExpectedLineVersion!.Value, command.ActorUserId), token);
                else
                {
                    var line = change.Line!;
                    result = await SaveLineAsync(new(rowOperation, command.WeekId, change.LineId,
                        week.Version, change.ExpectedLineVersion, line.PlannedDate, line.ProductId, line.Quantity,
                        line.OrderReference1, line.OrderReference2, line.OrderReference3, line.Notes,
                        command.ActorUserId, "", line.OriginalType, line.OriginalAnnotation1,
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
            week.Version += (uint)openingChanges.Count;
            db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fingerprint, week.Id,
                null, "draft-batch-changed", JsonSerializer.Serialize(beforeOpenings), JsonSerializer.Serialize(new { Count = command.Changes.Count + openingChanges.Count, Openings = openingChanges }),
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
