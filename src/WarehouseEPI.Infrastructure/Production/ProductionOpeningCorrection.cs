using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionOpeningCorrection(Guid OperationId, Guid WeekId, uint ExpectedWeekVersion,
    IReadOnlyList<ProductionOpeningChange> Changes, Guid ActorId, string Reason, string Fingerprint = "", string Pin = "");
public sealed record ProductionWeekOpeningReview(bool CanConfirm, string Fingerprint, IReadOnlyList<string> Errors,
    ProductionDailySummary? Before = null, ProductionDailySummary? After = null,
    ProductionWeekClose? BeforeClose = null, ProductionWeekClose? AfterClose = null);

public sealed partial class ProductionDailyScheduleService
{
    public async Task<ProductionWeekOpeningReview> PreviewOpeningCorrectionAsync(ProductionOpeningCorrection command, CancellationToken token = default)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
        if (!await IsAdminAsync(command.ActorId, token) || week is null || !week.ExplicitCarryover ||
            week.Status != ProductionScheduleWeekStatus.Open || command.ExpectedWeekVersion != week.Version)
            return new(false, "", ["La semana cambió o no admite esta corrección. Actualiza y revisa de nuevo."]);
        if (command.Changes.Count is < 1 or > 100 || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 500)
            return new(false, "", ["Revisa entre 1 y 100 cambios e indica un motivo de hasta 500 caracteres."]);
        var savedOpenings = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.WeekId == week.Id && x.Quantity > 0).ToListAsync(token);
        var allChanges = savedOpenings.Where(x => !command.Changes.Any(c => c.SourceWeekId == x.SourceWeekId && c.SourceLineId == x.SourceLineId && c.Area == x.Area))
            .Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Quantity, x.SourceFingerprint)).Concat(command.Changes).ToArray();
        var errors = await new ProductionWeekOpeningService(db).ValidateAsync(week.Id, allChanges, token);
        if (errors.Count > 0) return new(false, "", errors);
        var captures = await db.ProductionDailyCaptures.AsNoTracking().Where(x => x.WeekId == week.Id)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Status, x.Quantity }).ToListAsync(token);
        var fingerprint = Fingerprint(new { command.WeekId, week.Version, command.Changes, command.Reason, captures });
        var balance = new ProductionDailyBalanceService(db);
        return new(true, fingerprint, [], await balance.GetDailySummaryAsync(week.Id, new(week.WeekStart), token),
            await balance.GetDailySummaryAsync(week.Id, new(week.WeekStart), new(week.WeekStart, [], [], Openings: command.Changes), token),
            await balance.GetWeekCloseAsync(week.Id, new(week.WeekEnd), token),
            await balance.GetWeekCloseAsync(week.Id, new(week.WeekEnd), new(week.WeekStart, [], [], Openings: command.Changes), token));
    }

    public async Task<ProductionDailyCommandResult> ConfirmOpeningCorrectionAsync(ProductionOpeningCorrection command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code != "ADMIN" || user.Id != command.ActorId)
            return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["El NIP ADMIN no corresponde a la sesión actual."]);
        var fingerprint = Fingerprint(command with { Pin = "" });
        var prior = await db.ProductionScheduleRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.RequestFingerprint == fingerprint ? new(ProductionDailyCommandStatus.Success, command.WeekId) : new(ProductionDailyCommandStatus.IdempotencyConflict);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        var preview = await PreviewOpeningCorrectionAsync(command, token);
        if (!preview.CanConfirm) return new(ProductionDailyCommandStatus.ValidationFailed, Errors: preview.Errors);
        if (preview.Fingerprint != command.Fingerprint) return new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: ["Las capturas cambiaron. Revisa nuevamente la corrección."]);
        try
        {
            await new ProductionWeekOpeningService(db).ApplyAsync(command.WeekId, command.Changes, token);
            var week = await db.ProductionScheduleWeeks.SingleAsync(x => x.Id == command.WeekId, token);
            week.Version++;
            db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fingerprint, week.Id, null, "opening-corrected",
                JsonSerializer.Serialize(new { preview.Before, preview.BeforeClose }),
                JsonSerializer.Serialize(new { command.Reason, command.Changes, preview.After, preview.AfterClose }), command.ActorId));
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, week.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException || exception.GetBaseException() is Npgsql.PostgresException { SqlState: "40001" or "40P01" })
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        }
    }
}

