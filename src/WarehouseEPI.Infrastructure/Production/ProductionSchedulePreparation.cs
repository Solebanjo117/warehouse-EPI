using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyScheduleService
{
    public async Task<ProductionScheduleWorkspaceReview> PreviewNewWeekAsync(
        CreatePreparedProductionScheduleWeekCommand command, CancellationToken token = default)
    {
        if (!await IsAdminAsync(command.ActorUserId, token))
            return new(false, "", ["La programación requiere un usuario ADMIN autenticado."]);
        if (command.OperationId == Guid.Empty || command.WeekStart == DateOnly.MinValue || command.WeekStart.DayOfWeek != DayOfWeek.Monday ||
            command.WeekStart > DateOnly.MaxValue.AddDays(-ProductionWeekCalendar.LastDayOffset))
            return new(false, "", ["La semana debe iniciar en lunes."]);
        if (await db.ProductionScheduleWeeks.AnyAsync(x => x.WeekStart == command.WeekStart, token))
            return new(false, "", ["Ya existe una programación para esa semana."]);
        var openings = command.Openings ?? [];
        var initials = command.InitialBalances ?? [];
        var end = command.WeekStart.AddDays(ProductionWeekCalendar.LastDayOffset);
        var errors = await ValidateForPublicationAsync(new() { RequestFingerprint = "", WeekStart = command.WeekStart, WeekEnd = end }, token);
        var ids = command.Lines.Select(x => x.ProductId).Concat(initials.Select(x => x.ProductId)).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit)
            .Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(token);
        foreach (var line in command.Lines)
        {
            var product = products.SingleOrDefault(x => x.Id == line.ProductId);
            if (product is null || !product.IsActive || !PreparedLineValid(line, command.WeekStart, end, product.BaseUnit.AllowsDecimals))
                errors.Add("Revisa SKU, día, cantidad y detalles de los productos preparados.");
        }
        errors.AddRange(await new ProductionInitialBalanceService(db).ValidateAsync(null, initials, token));
        var openingService = new ProductionWeekOpeningService(db);
        if (openings.Count > 0) errors.AddRange(await openingService.ValidateAsync(command.WeekStart, openings, token));
        if (errors.Count > 0) return new(false, "", errors.Distinct().ToArray());
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var stageIds = new[] { config.CuttingStageId, config.SewingStageId, config.ReadyToPackStageId };
        var shiftIds = new[] { config.Shift1Id, config.Shift2Id };
        var stages = await db.ProductionStages.AsNoTracking().Where(x => stageIds.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.IsActive }).ToListAsync(token);
        var shifts = await db.ProductionShifts.AsNoTracking().Where(x => shiftIds.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.IsActive }).ToListAsync(token);
        var options = openings.Count > 0 ? await openingService.OptionsAsync(command.WeekStart, token) : [];
        return new(true, Fingerprint(new
        {
            Request = command with { ReviewedFingerprint = "" },
            Configuration = new { config.Version, stageIds, shiftIds, stages, shifts },
            Products = products.Select(x => new { x.Id, x.Sku, x.IsActive, x.BaseUnitId, x.BaseUnit.Code, x.BaseUnit.AllowsDecimals }),
            Sources = options.Where(o => openings.Any(c => c.SourceWeekId == o.SourceWeekId && c.SourceLineId == o.SourceLineId && c.Area == o.Area))
                .OrderBy(x => x.SourceWeekId).ThenBy(x => x.SourceLineId).ThenBy(x => x.Area)
                .Select(x => new { x.SourceWeekId, x.SourceLineId, x.Area, x.Fingerprint })
        }), []);
    }

    private static bool PreparedLineValid(ProductionScheduleBatchLine line, DateOnly start, DateOnly end, bool allowsDecimals) =>
        line.PlannedDate >= start && line.PlannedDate <= end && line.Quantity > 0 &&
        line.Quantity <= 99999999999999.9999m && decimal.Round(line.Quantity, 4) == line.Quantity &&
        (allowsDecimals || decimal.Truncate(line.Quantity) == line.Quantity) &&
        !new[] { line.OrderReference1, line.OrderReference2, line.OrderReference3, line.OriginalType }.Any(x => x?.Length > 120) &&
        !new[] { line.Notes, line.OriginalAnnotation1, line.OriginalAnnotation2 }.Any(x => x?.Length > 500);

    public async Task<ProductionDailyCommandResult> CreatePreparedWeekAsync(
        CreatePreparedProductionScheduleWeekCommand command, string adminPin, CancellationToken token = default)
    {
        if (!await IsAdminAsync(command.ActorUserId, token)) return Invalid("La programación requiere un usuario ADMIN autenticado.");
        var fingerprint = command.InitialBalances is null
            ? Fingerprint(new
            {
                command.OperationId,
                command.WeekStart,
                command.Lines,
                command.ActorUserId,
                command.Openings,
                command.ReviewedFingerprint
            }) : Fingerprint(command);
        var prior = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.RequestFingerprint == fingerprint
            ? new(ProductionDailyCommandStatus.Success, prior.Id) : new(ProductionDailyCommandStatus.IdempotencyConflict);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        try
        {
            // A retry can finish between the first lookup and this transaction's snapshot.
            prior = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            if (prior is not null) return await CancelAbortAsync(transaction, prior.RequestFingerprint == fingerprint
                ? new(ProductionDailyCommandStatus.Success, prior.Id) : new(ProductionDailyCommandStatus.IdempotencyConflict), token);
            var review = await PreviewNewWeekAsync(command, token);
            if (!review.CanConfirm) return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ValidationFailed, Errors: review.Errors), token);
            if (string.IsNullOrEmpty(command.ReviewedFingerprint) || command.ReviewedFingerprint != review.Fingerprint)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ConcurrencyConflict,
                    Errors: ["La preparación o los pendientes cambiaron. Revisa nuevamente antes de guardar."]), token);
            var user = await pins.AuthenticateAsync(adminPin, token);
            if (user?.Role.Code != "ADMIN" || user.Id != command.ActorUserId)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.InvalidPin,
                    Errors: ["El NIP ADMIN no corresponde a la sesión actual."]), token);
            var created = await CreateWeekAsync(new(Derive(command.OperationId, command.ActorUserId, "prepare-create"), command.WeekStart, command.ActorUserId), token);
            if (!created.Success) return await CancelAbortAsync(transaction, created, token);
            var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == created.Id, token);
            week.OperationId = command.OperationId;
            week.RequestFingerprint = fingerprint;
            var changes = command.Lines.Select(line => new ProductionScheduleDraftChange("add", null, null, line)).ToArray();
            if (changes.Length + (command.Openings?.Count ?? 0) + (command.InitialBalances?.Count ?? 0) > 0)
            {
                var saved = await SaveDraftChangesAsync(new(Derive(command.OperationId, week.Id, "prepare-save"), week.Id,
                    week.Version, changes, command.ActorUserId, command.Openings, InitialBalances: command.InitialBalances), token);
                if (!saved.Success) return await CancelAbortAsync(transaction, saved, token);
            }
            var published = await PublishAsync(new(Derive(command.OperationId, week.Id, "prepare-publish"), week.Id,
                week.Version, adminPin, command.ActorUserId), token);
            if (!published.Success) return await CancelAbortAsync(transaction, published, token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, week.Id);
        }
        catch (Exception error) when (error is DbUpdateException || error.GetBaseException() is PostgresException { SqlState: "40001" or "40P01" })
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            if (transaction is not null) await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
            var completed = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            if (completed is not null) return completed.RequestFingerprint == fingerprint
                ? new(ProductionDailyCommandStatus.Success, completed.Id) : new(ProductionDailyCommandStatus.IdempotencyConflict);
            return new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: ["La semana cambió. Actualiza y revisa de nuevo."]);
        }
    }
}
