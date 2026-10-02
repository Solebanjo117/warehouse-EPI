using System.Data;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionMatrixRow(Guid ProductId, ProductionDailyArea Area, decimal Quantity, string? Notes);
public sealed record ProductionMatrixCommand(Guid OperationId, DateOnly Date, Guid ShiftId,
    IReadOnlyList<ProductionMatrixRow> Rows, string ReviewedFingerprint = "", string Pin = "");
public sealed record ProductionMatrixItem(ProductionDailyArea Area, Guid ProductId, ProductionDailyCapturePreview Preview);
public sealed record ProductionMatrixPreview(bool CanConfirm, string Fingerprint,
    IReadOnlyList<ProductionMatrixItem> Items, IReadOnlyList<string> Errors);

public sealed partial class ProductionDailyCaptureService
{
    private static ProductionMatrixRow[] MatrixRows(ProductionMatrixCommand command) => command.Rows
        .Where(x => x.Quantity != 0).OrderBy(x => x.Area).ThenBy(x => x.ProductId).ToArray();

    private static string MatrixRequestFingerprint(ProductionMatrixCommand command) =>
        Fingerprint(new { Kind = "matrix-v2", command.Date, command.ShiftId, Rows = MatrixRows(command) });

    public async Task<ProductionDailyCommandResult?> FindRecordedMatrixAsync(ProductionMatrixCommand command, CancellationToken token = default)
    {
        var prior = await db.ProductionCaptureSubmissions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        return prior is null ? null : prior.RequestFingerprint == MatrixRequestFingerprint(command)
            ? new(ProductionDailyCommandStatus.Success, prior.Id) : new(ProductionDailyCommandStatus.IdempotencyConflict);
    }

    public async Task<ProductionMatrixPreview> PreviewMatrixAsync(ProductionMatrixCommand command, CancellationToken token = default)
    {
        var rows = MatrixRows(command);
        var errors = new List<string>();
        if (command.OperationId == Guid.Empty || rows.Length is 0 or > 300 ||
            rows.Select(x => x.ProductId).Distinct().Count() > 100 ||
            rows.Select(x => (x.ProductId, x.Area)).Distinct().Count() != rows.Length ||
            rows.Any(x => x.ProductId == Guid.Empty || !Enum.IsDefined(x.Area)))
            errors.Add("Selecciona entre 1 y 100 productos sin repetir.");
        if (rows.Any(x => x.Notes?.Length > 500)) errors.Add("Usa como máximo 500 caracteres en las notas.");
        var items = new List<ProductionMatrixItem>();
        if (errors.Count == 0)
            foreach (var row in rows)
            {
                var preview = await PreviewAsync(new(command.Date, row.Area, command.ShiftId, row.ProductId, row.Quantity), token);
                items.Add(new(row.Area, row.ProductId, preview));
                errors.AddRange(preview.Blockers.Select(x => $"{preview.Product ?? row.ProductId.ToString()} · {row.Area}: {x}"));
            }
        var ids = items.SelectMany(x => x.Preview.Allocations).Select(x => x.WorkOrderId).Distinct().ToArray();
        var versions = await db.ProductionWorkOrders.AsNoTracking().Where(x => ids.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Version }).ToListAsync(token);
        return new(errors.Count == 0, Fingerprint(new { command.Date, command.ShiftId, rows, items, versions }), items, errors);
    }

    public async Task<ProductionDailyCommandResult> ConfirmMatrixAsync(ProductionMatrixCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user is null) return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["NIP inválido."]);
        if (!RoleAccess.CanCaptureProduction(user.Role.Code)) return new(ProductionDailyCommandStatus.RoleNotAllowed, Errors: [RoleAccess.ProductionWarning]);
        async Task<ProductionDailyCommandResult?> Prior()
        {
            var prior = await db.ProductionCaptureSubmissions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            return prior is null ? null : prior.RequestFingerprint == MatrixRequestFingerprint(command) && prior.ResponsibleUserId == user.Id
                ? new(ProductionDailyCommandStatus.Success, prior.Id) : new(ProductionDailyCommandStatus.IdempotencyConflict);
        }
        if (await Prior() is { } recorded) return recorded;
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        try
        {
            var preview = await PreviewMatrixAsync(command, token);
            if (!preview.CanConfirm) return new(ProductionDailyCommandStatus.ValidationFailed, Errors: preview.Errors);
            if (preview.Fingerprint != command.ReviewedFingerprint)
                return new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: ["La disponibilidad cambió. Revisa la tanda actualizada antes de confirmar."]);
            var submission = new ProductionCaptureSubmission
            {
                OperationId = command.OperationId,
                RequestFingerprint = MatrixRequestFingerprint(command),
                ResponsibleUserId = user.Id,
                RecordedAt = timeProvider.GetUtcNow()
            };
            db.ProductionCaptureSubmissions.Add(submission);
            foreach (var row in MatrixRows(command))
            {
                var operation = Derive(command.OperationId, row.ProductId, Guid.Empty, $"matrix-v2-{(int)row.Area}");
                var result = await ConfirmAsync(new(operation, command.Date, row.Area, command.ShiftId,
                    row.ProductId, row.Quantity, row.Notes, command.Pin), token);
                if (!result.Success)
                {
                    if (transaction is not null) await transaction.RollbackAsync(token);
                    db.ChangeTracker.Clear();
                    return result;
                }
                db.Set<ProductionCaptureSubmissionItem>().Add(new() { SubmissionId = submission.Id, CaptureId = result.Id!.Value });
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
