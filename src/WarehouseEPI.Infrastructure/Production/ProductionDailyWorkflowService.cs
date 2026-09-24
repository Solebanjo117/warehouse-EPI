using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionCopyRow(Guid SourceLineId, DateOnly Date, decimal Quantity);

public sealed record CopyProductionWeekCommand(Guid OperationId, Guid WeekId, uint ExpectedVersion, Guid SourceWeekId,
    uint SourceVersion, IReadOnlyList<ProductionCopyRow> Rows, Guid ActorId, string AdminPin = "");

public sealed record SaveProductionCarryoverPlanCommand(Guid OperationId, Guid WeekId, uint WeekVersion, Guid? Id,
    uint Version, DateOnly Date, Guid ProductId, ProductionDailyArea Area, decimal Quantity, decimal ExpectedAvailable, Guid ActorId);

public sealed record ProductionCarryoverSuggestion(Guid ProductId, string Sku, ProductionDailyArea Area, decimal Available);

public sealed partial class ProductionDailyScheduleService
{
    private ProductionDailyCaptureService DailyCaptures() => new(db, pins, movements,
        new WarehouseClock(new WarehouseSettingsService(db)), timeProvider);
    public async Task<IReadOnlyList<ProductionCarryoverSuggestion>> GetCarryoverSuggestionsAsync(Guid weekId, CancellationToken token = default)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weekId, token);
        if (week is null) return [];
        var result = new List<ProductionCarryoverSuggestion>();
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
            result.AddRange((await DailyCaptures().GetAvailabilityAsync(week.WeekStart, area, true, token))
                .Select(x => new ProductionCarryoverSuggestion(x.ProductId, x.Sku, area, x.Available)));
        return result.OrderBy(x => x.Sku).ThenBy(x => x.Area).ToArray();
    }

    public async Task<ProductionDailyCommandResult> SaveCarryoverPlanAsync(SaveProductionCarryoverPlanCommand command, CancellationToken token = default)
    {
        if (!await IsAdminAsync(command.ActorId, token)) return Invalid("La programación requiere un usuario ADMIN autenticado.");
        var fp = Fingerprint(command);
        var prior = await db.ProductionScheduleRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.RequestFingerprint == fp
            ? new(ProductionDailyCommandStatus.Success, JsonSerializer.Deserialize<ProductionCarryoverPlan>(prior.AfterJson)!.Id)
            : new(ProductionDailyCommandStatus.IdempotencyConflict);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        var week = await db.ProductionScheduleWeeks.SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
        if (week is null) return new(ProductionDailyCommandStatus.NotFound);
        if (week.ExplicitCarryover) return Invalid("Selecciona el arrastre en Arrastre inicial de la preparación semanal.");
        if (week.Status == ProductionScheduleWeekStatus.Closed) return Invalid("Reabre la semana antes de modificarla.");
        if (week.Version != command.WeekVersion) return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        if (command.Date < week.WeekStart || command.Date > week.WeekEnd || !Enum.IsDefined(command.Area) ||
            command.Quantity < 0 || command.Quantity > 99999999999999.9999m || decimal.Round(command.Quantity, 4) != command.Quantity)
            return Invalid("Revisa el día y la cantidad del arrastre programado.");
        var product = await db.Products.Include(x => x.BaseUnit).SingleOrDefaultAsync(x => x.Id == command.ProductId && x.IsActive, token);
        if (product is null || !product.BaseUnit.AllowsDecimals && decimal.Truncate(command.Quantity) != command.Quantity)
            return Invalid("Selecciona un SKU activo y una cantidad compatible con su unidad.");
        var available = (await GetCarryoverSuggestionsAsync(week.Id, token)).SingleOrDefault(x => x.ProductId == command.ProductId && x.Area == command.Area)?.Available ?? 0;
        var other = await db.ProductionCarryoverPlans.Where(x => x.WeekId == week.Id && x.ProductId == command.ProductId &&
            x.Area == command.Area && x.Id != command.Id).SumAsync(x => x.Quantity, token);
        if (command.Quantity > 0 && (available != command.ExpectedAvailable || command.Quantity + other > available))
            return Invalid("El pendiente disponible cambió o no alcanza. Revisa el arrastre programado.");
        var plan = command.Id.HasValue ? await db.ProductionCarryoverPlans.SingleOrDefaultAsync(x => x.Id == command.Id && x.WeekId == week.Id, token) : null;
        if (command.Id.HasValue && (plan is null || plan.Version != command.Version)) return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        if (await db.ProductionCarryoverPlans.AnyAsync(x => x.WeekId == week.Id && x.PlannedDate == command.Date && x.ProductId == command.ProductId && x.Area == command.Area && x.Id != command.Id, token))
            return Invalid("Ya existe un arrastre programado para ese producto, día y área. Edita el existente.");
        var before = plan is null ? "{}" : JsonSerializer.Serialize(plan);
        if (plan is null) { plan = new ProductionCarryoverPlan { WeekId = week.Id }; db.ProductionCarryoverPlans.Add(plan); }
        plan.PlannedDate = command.Date; plan.ProductId = command.ProductId; plan.Area = command.Area; plan.Quantity = command.Quantity;
        plan.UpdatedByUserId = command.ActorId; plan.UpdatedAt = timeProvider.GetUtcNow(); plan.Version++; week.Version++;
        db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fp, week.Id, null, "carryover-planned", before, JsonSerializer.Serialize(plan), command.ActorId));
        try
        {
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, plan.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException || exception.GetBaseException() is Npgsql.PostgresException { SqlState: "40001" or "40P01" }) { if (transaction is not null) await transaction.RollbackAsync(token); db.ChangeTracker.Clear(); return new(ProductionDailyCommandStatus.ConcurrencyConflict); }
    }

    public async Task<ProductionDailyCommandResult> CopyWeekAsync(CopyProductionWeekCommand command, CancellationToken token = default)
    {
        if (!await IsAdminAsync(command.ActorId, token)) return Invalid("La programación requiere un usuario ADMIN autenticado.");
        var fp = Fingerprint(command with { AdminPin = "" });
        var prior = await db.ProductionScheduleRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.RequestFingerprint == fp ? new(ProductionDailyCommandStatus.Success, command.WeekId) : new(ProductionDailyCommandStatus.IdempotencyConflict);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        var week = await db.ProductionScheduleWeeks.SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
        var source = await db.ProductionScheduleWeeks.Include(x => x.Lines).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
            .SingleOrDefaultAsync(x => x.Id == command.SourceWeekId, token);
        if (week is null || source is null || source.WeekStart >= week.WeekStart) return Invalid("Selecciona una semana anterior válida.");
        if (week.Version != command.ExpectedVersion || source.Version != command.SourceVersion) return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        if (week.Status == ProductionScheduleWeekStatus.Closed) return Invalid("Reabre la semana antes de modificarla.");
        if (command.Rows.Count is 0 or > 100 || command.Rows.Select(x => x.SourceLineId).Distinct().Count() != command.Rows.Count)
            return Invalid("Selecciona entre 1 y 100 líneas sin repetir.");
        foreach (var row in command.Rows)
        {
            var line = source.Lines.SingleOrDefault(x => x.Id == row.SourceLineId && !x.IsCancelled && !x.IsCarryover && !x.IsExtra);
            if (line is null || !line.Product.IsActive || row.Date < week.WeekStart || row.Date > week.WeekEnd || row.Quantity <= 0 ||
                row.Quantity > 99999999999999.9999m || decimal.Round(row.Quantity, 4) != row.Quantity ||
                !line.Product.BaseUnit.AllowsDecimals && decimal.Truncate(row.Quantity) != row.Quantity)
                return Invalid("Revisa productos, días y cantidades de las líneas seleccionadas.");
        }
        try
        {
            foreach (var row in command.Rows)
            {
                var line = source.Lines.Single(x => x.Id == row.SourceLineId);
                var result = await SaveLineAsync(new(Derive(command.OperationId, row.SourceLineId, "copy"), week.Id, null, week.Version, null,
                    row.Date, line.ProductId, row.Quantity, null, null, null, null, command.ActorId, command.AdminPin), token);
                if (!result.Success) { if (transaction is not null) await transaction.RollbackAsync(token); db.ChangeTracker.Clear(); return result; }
            }
            db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fp, week.Id, null, "products-copied", "{}", JsonSerializer.Serialize(command.Rows), command.ActorId));
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, week.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException || exception.GetBaseException() is Npgsql.PostgresException { SqlState: "40001" or "40P01" }) { if (transaction is not null) await transaction.RollbackAsync(token); db.ChangeTracker.Clear(); return new(ProductionDailyCommandStatus.ConcurrencyConflict); }
    }

    internal async Task<ProductionDailyCommandResult> CreateExtraAsync(ProductionDailyCapture capture, decimal quantity,
        Guid actorId, string pin, CancellationToken token)
    {
        var week = await db.ProductionScheduleWeeks.Include(x => x.Lines).SingleAsync(x => x.Id == capture.WeekId, token);
        var product = await db.Products.SingleAsync(x => x.Id == capture.ProductId, token);
        var line = new ProductionScheduleLine
        {
            WeekId = week.Id, Sequence = week.Lines.Select(x => x.Sequence).DefaultIfEmpty().Max() + 1,
            ProductId = product.Id, PlannedDate = capture.EffectiveDate, Quantity = quantity, IsExtra = true,
            Notes = "Producción extra de captura diaria"
        };
        db.ProductionScheduleLines.Add(line);
        var command = new SaveProductionScheduleLineCommand(capture.OperationId, week.Id, null, week.Version, null,
            capture.EffectiveDate, product.Id, quantity, null, null, null, line.Notes, actorId, pin);
        var result = await CreatePublishedLineAsync(line, product, command, token);
        if (!result.Success) return result;
        week.Version++;
        db.ProductionScheduleRevisions.Add(Revision(Derive(capture.OperationId, line.Id, "extra"),
            Fingerprint(new { capture.Id, quantity }), week.Id, line.Id, "extra-created", "{}",
            JsonSerializer.Serialize(new { CaptureId = capture.Id, line.WorkOrderId, quantity }), actorId));
        await db.SaveChangesAsync(token);
        return new(ProductionDailyCommandStatus.Success, line.Id);
    }

    private async Task<ProductionDailyCommandResult> CreatePublishedLineAsync(ProductionScheduleLine line, Product product,
        SaveProductionScheduleLineCommand command, CancellationToken token)
    {
        var order = await BuildDailyOrderAsync(Derive(command.OperationId, line.Id, "create"), product, command.Quantity,
            command.OrderReference1, command.PlannedDate, command.Notes, null, command.ActorUserId, "Producto agregado a semana abierta.", token);
        var released = await ReleaseDailyOrderAsync(order, Derive(command.OperationId, line.Id, "release"), command.ActorUserId,
            "Producto agregado a semana abierta.", token);
        if (released.Status != ProductionCommandStatus.Success) return Map(released, "Programa");
        var trace = new ProductionTraceabilityService(db, pins, new ProductionMaterialService(db, pins, movements, timeProvider), timeProvider);
        var batch = await trace.CreateBatchAsync(new(Derive(command.OperationId, line.Id, "batch"), order.Id, command.Quantity, order.Version, command.AdminPin), token);
        if (!batch.Success) return new(ProductionDailyCommandStatus.ValidationFailed, Errors: batch.Errors);
        line.WorkOrderId = order.Id;
        return new(ProductionDailyCommandStatus.Success, order.Id);
    }
}
