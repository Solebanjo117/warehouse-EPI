using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyScheduleService
{
    public async Task<ProductionDailyCommandResult> SaveBatchAsync(
        SaveProductionScheduleBatchCommand command, CancellationToken token = default)
    {
        if (!await IsAdminAsync(command.ActorUserId, token))
            return Invalid("La programación requiere un usuario ADMIN autenticado.");
        if (command.Lines is not { Count: > 0 and <= 100 })
            return Invalid("Agrega entre 1 y 100 renglones antes de confirmar.");

        var fingerprint = Fingerprint(command with { AdminPin = "" });
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
            var week = await db.ProductionScheduleWeeks.SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
            if (week is null) return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.NotFound), token);
            if (week.Version != command.ExpectedWeekVersion)
                return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.ConcurrencyConflict), token);
            if (week.Status == ProductionScheduleWeekStatus.Closed)
                return await CancelAbortAsync(transaction, Invalid("Reabre la semana antes de modificarla."), token);
            var productIds = command.Lines.Select(x => x.ProductId).Distinct().ToArray();
            var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit)
                .Where(x => productIds.Contains(x.Id) && x.IsActive)
                .ToDictionaryAsync(x => x.Id, token);
            var issues = new List<string>();
            for (var index = 0; index < command.Lines.Count; index++)
            {
                var row = command.Lines[index];
                if (row.PlannedDate < week.WeekStart || row.PlannedDate > week.WeekEnd)
                    issues.Add($"Renglón {index + 1}: la fecha debe pertenecer a la semana seleccionada.");
                if (row.Quantity <= 0 || decimal.Round(row.Quantity, 4) != row.Quantity)
                    issues.Add($"Renglón {index + 1}: indica una cantidad positiva con hasta cuatro decimales.");
                if (!products.TryGetValue(row.ProductId, out var product))
                    issues.Add($"Renglón {index + 1}: selecciona un SKU activo del catálogo.");
                else if (!product.BaseUnit.AllowsDecimals && decimal.Truncate(row.Quantity) != row.Quantity)
                    issues.Add($"Renglón {index + 1}: la unidad del SKU no permite decimales.");
            }
            if (issues.Count > 0)
                return await CancelAbortAsync(transaction,
                    new(ProductionDailyCommandStatus.ValidationFailed, Errors: issues), token);
            if (week.Status == ProductionScheduleWeekStatus.Open)
            {
                var actor = await pins.AuthenticateAsync(command.AdminPin, token);
                if (actor?.Role.Code != "ADMIN" || actor.Id != command.ActorUserId)
                    return await CancelAbortAsync(transaction, new(ProductionDailyCommandStatus.InvalidPin,
                        Errors: ["El NIP ADMIN no corresponde a la sesión actual."]), token);
            }

            for (var index = 0; index < command.Lines.Count; index++)
            {
                var row = command.Lines[index];
                var rowKey = new byte[16];
                BitConverter.TryWriteBytes(rowKey, index);
                var rowOperation = Derive(command.OperationId, new Guid(rowKey), "batch-line");
                var saved = await SaveLineAsync(new(rowOperation, command.WeekId, null,
                    command.ExpectedWeekVersion + (uint)index, null, row.PlannedDate, row.ProductId,
                    row.Quantity, row.OrderReference1, row.OrderReference2, row.OrderReference3,
                    row.Notes, command.ActorUserId, command.AdminPin, row.OriginalType,
                    row.OriginalAnnotation1, row.OriginalAnnotation2, row.OriginalAnnotation1Kind,
                    row.OriginalAnnotation2Kind), token);
                if (!saved.Success)
                {
                    var errors = saved.Errors is { Count: > 0 }
                        ? saved.Errors.Select(x => $"Renglón {index + 1}: {x}").ToArray()
                        : [$"Renglón {index + 1}: no se pudo guardar."];
                    return await CancelAbortAsync(transaction, saved with { Errors = errors }, token);
                }
            }

            db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fingerprint,
                command.WeekId, null, "line-batch-created", "{}",
                JsonSerializer.Serialize(new { Count = command.Lines.Count }), command.ActorUserId));
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, command.WeekId);
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
