using System.Data;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionAvailableProduct(Guid ProductId, string Sku, string Description, decimal Available, decimal ToReconcile = 0);
public sealed record ProductionCaptureRow(Guid ProductId, decimal Quantity, string? Notes);
public sealed record ProductionCaptureGroupCommand(Guid OperationId, DateOnly Date, ProductionDailyArea Area, Guid ShiftId,
    IReadOnlyList<ProductionCaptureRow> Rows, string ReviewedFingerprint = "", string Pin = "");
public sealed record ProductionCaptureGroupPreview(bool CanConfirm, string Fingerprint,
    IReadOnlyList<ProductionDailyCapturePreview> Products, IReadOnlyList<string> Errors);

public sealed partial class ProductionDailyCaptureService
{
    public async Task<IReadOnlyList<ProductionAvailableProduct>> GetAvailabilityAsync(DateOnly date, ProductionDailyArea area,
        bool priorOnly = false, CancellationToken token = default)
    {
        if (!Enum.IsDefined(area)) return [];
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.WeekStart == monday, token);
        if (week is null) return [];
        var balance = await new ProductionDailyBalanceService(db).GetAsync(week.Id, true, priorOnly, token);
        var products = await db.Products.AsNoTracking().Where(x => x.IsActive).Select(x => x.Id).ToListAsync(token);
        return balance!.Rows.Where(x => x.Date == date && products.Contains(x.ProductId)).Select(row =>
        {
            var value = area switch { ProductionDailyArea.Cutting => row.Cutting, ProductionDailyArea.Sewing => row.Sewing, _ => row.ReadyToPack };
            return new ProductionAvailableProduct(row.ProductId, row.Sku, row.Description ?? "", value.Pending, value.ToReconcile);
        }).Where(x => x.Available > 0 || !priorOnly && x.ToReconcile > 0).OrderBy(x => x.Sku).ToArray();
    }

    public async Task<ProductionCaptureGroupPreview> PreviewGroupAsync(ProductionCaptureGroupCommand command, CancellationToken token = default)
    {
        var rows = command.Rows.Where(x => x.Quantity != 0).OrderBy(x => x.ProductId).ToArray();
        var errors = new List<string>();
        if (!Enum.IsDefined(command.Area)) errors.Add("El área seleccionada no está configurada.");
        if (rows.Length is 0 or > 100 || rows.Select(x => x.ProductId).Distinct().Count() != rows.Length)
            errors.Add("Selecciona entre 1 y 100 productos sin repetir.");
        if (rows.Any(x => x.Notes?.Length > 500)) errors.Add("Usa como máximo 500 caracteres en las notas.");
        var previews = new List<ProductionDailyCapturePreview>();
        if (errors.Count == 0)
            foreach (var row in rows)
            {
                var preview = await PreviewAsync(new(command.Date, command.Area, command.ShiftId, row.ProductId, row.Quantity), token);
                previews.Add(preview);
                errors.AddRange(preview.Blockers.Select(x => $"{preview.Product ?? row.ProductId.ToString()}: {x}"));
            }
        var ids = previews.SelectMany(x => x.Allocations).Select(x => x.WorkOrderId).Distinct().ToArray();
        var versions = await db.ProductionWorkOrders.AsNoTracking().Where(x => ids.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Version }).ToListAsync(token);
        var fingerprint = Fingerprint(new { command.Date, command.Area, command.ShiftId, rows, previews, versions });
        return new(errors.Count == 0, fingerprint, previews, errors);
    }

    public async Task<ProductionDailyCommandResult> ConfirmGroupAsync(ProductionCaptureGroupCommand command, CancellationToken token = default)
    {
        var rows = command.Rows.Where(x => x.Quantity != 0).OrderBy(x => x.ProductId).ToArray();
        var fp = Fingerprint(new { command.Date, command.Area, command.ShiftId, rows });
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code is not ("ADMIN" or "OPERATOR")) return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["NIP inválido."]);
        async Task<ProductionDailyCommandResult?> Prior()
        {
            var prior = await db.ProductionCaptureSubmissions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            return prior is null ? null : prior.RequestFingerprint == fp && prior.ResponsibleUserId == user.Id
                ? new(ProductionDailyCommandStatus.Success, prior.Id) : new(ProductionDailyCommandStatus.IdempotencyConflict);
        }
        if (await Prior() is { } previous) return previous;
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        try
        {
            var preview = await PreviewGroupAsync(command, token);
            if (!preview.CanConfirm) return new(ProductionDailyCommandStatus.ValidationFailed, Errors: preview.Errors);
            if (preview.Fingerprint != command.ReviewedFingerprint)
                return new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: ["La disponibilidad cambió. Revisa la tanda actualizada antes de confirmar."]);
            var submission = new ProductionCaptureSubmission
            {
                OperationId = command.OperationId,
                RequestFingerprint = fp,
                ResponsibleUserId = user.Id,
                RecordedAt = timeProvider.GetUtcNow()
            };
            db.ProductionCaptureSubmissions.Add(submission);
            foreach (var row in rows)
            {
                var operation = Derive(command.OperationId, row.ProductId, Guid.Empty, "group-capture");
                var result = await ConfirmAsync(new(operation, command.Date, command.Area, command.ShiftId,
                    row.ProductId, row.Quantity, row.Notes, command.Pin), token);
                if (!result.Success)
                {
                    if (transaction is not null) await transaction.RollbackAsync(token);
                    db.ChangeTracker.Clear();
                    return result;
                }
                var item = new ProductionCaptureSubmissionItem { SubmissionId = submission.Id, CaptureId = result.Id!.Value };
                db.Set<ProductionCaptureSubmissionItem>().Add(item);
            }
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, submission.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException || exception.GetBaseException() is Npgsql.PostgresException { SqlState: "40001" or "40P01" })
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return await Prior() ?? new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: ["La disponibilidad cambió. Revisa la tanda actualizada antes de confirmar."]);
        }
    }
}
