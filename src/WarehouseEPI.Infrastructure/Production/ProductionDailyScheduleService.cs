using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyScheduleService(
    WarehouseDbContext db,
    UserPinService pins,
    InventoryMovementService movements,
    TimeProvider timeProvider)
{
    public async Task<ProductionDailyConfigurationView> GetConfigurationAsync(CancellationToken token = default)
    {
        var row = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        return View(row);
    }

    public async Task<ProductionDailyCommandResult> ConfigureAsync(
        ConfigureProductionDailyCommand command, CancellationToken token = default)
    {
        var fingerprint = Fingerprint(command);
        if (!await IsAdminAsync(command.ActorUserId, token))
            return Invalid("La configuración requiere un usuario ADMIN autenticado.");
        var stageIds = new[] { command.CuttingStageId, command.SewingStageId, command.ReadyToPackStageId };
        var shiftIds = new[] { command.Shift1Id, command.Shift2Id };
        if (stageIds.Any(x => x == Guid.Empty) || stageIds.Distinct().Count() != 3 ||
            await db.ProductionStages.CountAsync(x => stageIds.Contains(x.Id) && x.IsActive, token) != 3)
            return Invalid("Selecciona tres procesos activos y diferentes.");
        if (shiftIds.Any(x => x == Guid.Empty) || shiftIds.Distinct().Count() != 2 ||
            await db.ProductionShifts.CountAsync(x => shiftIds.Contains(x.Id) && x.IsActive, token) != 2)
            return Invalid("Selecciona T1 y T2 entre los turnos activos.");

        var row = await db.ProductionDailyConfigurations.SingleAsync(x => x.Id == 1, token);
        if (row.LastOperationId == command.OperationId)
            return row.LastRequestFingerprint == fingerprint
                ? new(ProductionDailyCommandStatus.Success)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);
        if (row.Version != command.ExpectedVersion)
            return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        row.CuttingStageId = command.CuttingStageId;
        row.SewingStageId = command.SewingStageId;
        row.ReadyToPackStageId = command.ReadyToPackStageId;
        row.Shift1Id = command.Shift1Id;
        row.Shift2Id = command.Shift2Id;
        row.UpdatedByUserId = command.ActorUserId;
        row.UpdatedAt = timeProvider.GetUtcNow();
        row.LastOperationId = command.OperationId;
        row.LastRequestFingerprint = fingerprint;
        row.Version++;
        try
        {
            await db.SaveChangesAsync(token);
            return new(ProductionDailyCommandStatus.Success);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        }
    }

    public async Task<IReadOnlyList<ProductionScheduleWeekView>> ListWeeksAsync(CancellationToken token = default)
    {
        var weeks = await WeekQuery().OrderByDescending(x => x.WeekStart).Take(60).ToListAsync(token);
        return weeks.Select(View).ToArray();
    }

    public async Task<ProductionScheduleWeekView?> GetWeekAsync(Guid id, CancellationToken token = default)
    {
        var week = await WeekQuery().SingleOrDefaultAsync(x => x.Id == id, token);
        return week is null ? null : View(week);
    }

    public async Task<ProductionDailyCommandResult> CreateWeekAsync(
        CreateProductionScheduleWeekCommand command, CancellationToken token = default)
    {
        var fp = Fingerprint(command);
        var prior = await db.ProductionScheduleWeeks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null)
            return prior.RequestFingerprint == fp
                ? new(ProductionDailyCommandStatus.Success, prior.Id)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);
        if (!await IsAdminAsync(command.ActorUserId, token))
            return Invalid("La programación requiere un usuario ADMIN autenticado.");
        if (command.WeekStart.DayOfWeek != DayOfWeek.Monday)
            return Invalid("La semana debe iniciar en lunes.");
        if (await db.ProductionScheduleWeeks.AnyAsync(x => x.WeekStart == command.WeekStart, token))
            return Invalid("Ya existe una programación para esa semana.");
        var week = new ProductionScheduleWeek
        {
            OperationId = command.OperationId,
            RequestFingerprint = fp,
            WeekStart = command.WeekStart,
            WeekEnd = command.WeekStart.AddDays(5),
            CreatedByUserId = command.ActorUserId,
            CreatedAt = timeProvider.GetUtcNow()
        };
        db.ProductionScheduleWeeks.Add(week);
        await db.SaveChangesAsync(token);
        return new(ProductionDailyCommandStatus.Success, week.Id);
    }

    public async Task<ProductionDailyCommandResult> SaveLineAsync(
        SaveProductionScheduleLineCommand command, CancellationToken token = default)
    {
        var fp = Fingerprint(command with { AdminPin = "" });
        var prior = await db.ProductionScheduleRevisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null)
            return prior.RequestFingerprint == fp
                ? new(ProductionDailyCommandStatus.Success, prior.LineId)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);
        if (!await IsAdminAsync(command.ActorUserId, token))
            return Invalid("La programación requiere un usuario ADMIN autenticado.");
        var week = await db.ProductionScheduleWeeks.Include(x => x.Lines).ThenInclude(x => x.Product)
            .SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
        if (week is null) return new(ProductionDailyCommandStatus.NotFound);
        if (week.Status == ProductionScheduleWeekStatus.Closed)
            return Invalid("Reabre la semana antes de modificarla.");
        if (week.Version != command.ExpectedWeekVersion)
            return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        if (command.PlannedDate < week.WeekStart || command.PlannedDate > week.WeekEnd ||
            command.PlannedDate.DayOfWeek == DayOfWeek.Sunday)
            return Invalid("La fecha debe ser un lunes a sábado de la semana seleccionada.");
        if (command.Quantity <= 0 || decimal.Round(command.Quantity, 4) != command.Quantity)
            return Invalid("La cantidad debe ser positiva y admitir hasta cuatro decimales.");
        var product = await db.Products.Include(x => x.BaseUnit)
            .SingleOrDefaultAsync(x => x.Id == command.ProductId && x.IsActive, token);
        if (product is null) return Invalid("Selecciona un SKU activo del catálogo.");
        if (!product.BaseUnit.AllowsDecimals && decimal.Truncate(command.Quantity) != command.Quantity)
            return Invalid("La unidad del SKU no permite decimales.");
        if (week.Status == ProductionScheduleWeekStatus.Open && command.LineId is null)
        {
            var actor = await pins.AuthenticateAsync(command.AdminPin, token);
            if (actor?.Role.Code != "ADMIN" || actor.Id != command.ActorUserId)
                return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["El NIP ADMIN no corresponde a la sesión actual."]);
            var validation = await ValidateForPublicationAsync(week, token);
            if (validation.Count > 0) return new(ProductionDailyCommandStatus.ValidationFailed, Errors: validation);
        }

        ProductionScheduleLine line;
        string action;
        string before;
        var replacePublishedProduct = false;
        if (command.LineId is Guid lineId)
        {
            line = week.Lines.SingleOrDefault(x => x.Id == lineId) ?? throw new InvalidOperationException("La línea no pertenece a la semana.");
            if (line.IsExtra) return Invalid("Las órdenes extra se corrigen mediante el reverso de su captura.");
            if (line.Version != command.ExpectedLineVersion)
                return new(ProductionDailyCommandStatus.ConcurrencyConflict);
            if (week.Status == ProductionScheduleWeekStatus.Open && line.ProductId != command.ProductId && line.WorkOrderId.HasValue)
            {
                var hasActivity = await db.ProductionEvents.AnyAsync(x => x.WorkOrderId == line.WorkOrderId &&
                    (x.Type == ProductionEventType.Processed || x.Type == ProductionEventType.Reworked ||
                     x.Type == ProductionEventType.Delivered || x.Type == ProductionEventType.Received ||
                     x.Type == ProductionEventType.WarehouseReceived), token);
                hasActivity = hasActivity || await db.ProductionMaterialIssueLinks
                    .AnyAsync(x => x.WorkOrderId == line.WorkOrderId && x.Quantity > x.CancelledQuantity, token);
                if (hasActivity) return Invalid("El SKU ya tiene movimientos. Corrige la orden desde Producción avanzada.");
                var candidateWeek = new ProductionScheduleWeek
                {
                    RequestFingerprint = string.Empty,
                    WeekStart = week.WeekStart,
                    WeekEnd = week.WeekEnd
                };
                candidateWeek.Lines.Add(new ProductionScheduleLine
                {
                    Sequence = line.Sequence,
                    PlannedDate = command.PlannedDate,
                    ProductId = product.Id,
                    Product = product,
                    Quantity = command.Quantity,
                    StartArea = line.StartArea
                });
                var blockers = await ValidateForPublicationAsync(candidateWeek, token);
                if (blockers.Count > 0)
                    return new(ProductionDailyCommandStatus.ValidationFailed, Errors: blockers);
                replacePublishedProduct = true;
            }
            var processed = line.WorkOrderId.HasValue
                ? await EffectiveGoodAsync(line.WorkOrderId.Value, token)
                : 0m;
            if (command.Quantity < processed)
                return Invalid($"La cantidad no puede ser menor que lo ya procesado ({processed:0.####}).");
            before = JsonSerializer.Serialize(LineSnapshot(line));
            action = replacePublishedProduct ? "line-product-replaced" : "line-updated";
        }
        else
        {
            line = new ProductionScheduleLine
            {
                WeekId = week.Id,
                Sequence = week.Lines.Count == 0 ? 1 : week.Lines.Max(x => x.Sequence) + 1
            };
            week.Lines.Add(line);
            db.ProductionScheduleLines.Add(line);
            before = "{}";
            action = "line-created";
        }

        var ownsTransaction = week.Status == ProductionScheduleWeekStatus.Open &&
                              db.Database.IsRelational() && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            if (week.Status == ProductionScheduleWeekStatus.Open && command.LineId is null)
            {
                line.PlannedDate = command.PlannedDate; line.ProductId = product.Id; line.Quantity = command.Quantity;
                var created = await CreatePublishedLineAsync(line, product, command, token);
                if (!created.Success) return await AbortAsync(transaction, created, token);
            }
            else if (week.Status == ProductionScheduleWeekStatus.Open && line.WorkOrderId is Guid publishedOrderId)
            {
                if (replacePublishedProduct)
                {
                    var replaced = await ReplacePublishedOrderAsync(line, product, command, token);
                    if (replaced.Status != ProductionDailyCommandStatus.Success)
                    {
                        if (transaction is not null) await transaction.RollbackAsync(token);
                        return replaced;
                    }
                    line.WorkOrderId = replaced.Id;
                }
                else
                {
                    var order = await db.ProductionWorkOrders.Include(x => x.MaterialPlan)
                        .SingleAsync(x => x.Id == publishedOrderId, token);
                    var materials = order.MaterialPlan.Select(x => new ExecutionMaterial(x.Id,
                        decimal.Round(x.PlannedQuantity * command.Quantity / order.AuthorizedQuantity, 4,
                            MidpointRounding.AwayFromZero))).ToArray();
                    var adjusted = await new ProductionExecutionService(db, pins, timeProvider).ApplyAuthorizedAsync(
                        new ProductionExecutionCommand(Derive(command.OperationId, line.Id, "schedule-adjust"), order.Id,
                            order.Version, "adjust", string.Empty,
                            ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment),
                            "Actualización desde el programa semanal", Target: command.Quantity,
                            Authorized: command.Quantity, DueDate: command.PlannedDate, Materials: materials),
                        command.ActorUserId, token);
                    if (adjusted.Status != ProductionCommandStatus.Success)
                    {
                        if (transaction is not null) await transaction.RollbackAsync(token);
                        return Map(adjusted, $"Línea {line.Sequence}");
                    }
                    order.ExternalReference = Trim(string.Join(" / ", new[]
                        { command.OrderReference1, command.OrderReference2, command.OrderReference3 }.Where(x => !string.IsNullOrWhiteSpace(x))), 120);
                    order.Notes = Trim(command.Notes, 500);
                }
            }

            line.PlannedDate = command.PlannedDate;
            line.ProductId = command.ProductId;
            line.Quantity = command.Quantity;
            line.OrderReference1 = Trim(command.OrderReference1, 120);
            line.OrderReference2 = Trim(command.OrderReference2, 120);
            line.OrderReference3 = Trim(command.OrderReference3, 120);
            line.Notes = Trim(command.Notes, 500);
            line.Version++;
            week.Version++;
            db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fp, week.Id, line.Id,
                action, before, JsonSerializer.Serialize(LineSnapshot(line)), command.ActorUserId));
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, line.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException || exception.GetBaseException() is Npgsql.PostgresException { SqlState: "40001" or "40P01" })
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        }
    }

    public async Task<ProductionDailyCommandResult> PublishAsync(
        PublishProductionScheduleWeekCommand command, CancellationToken token = default)
    {
        var fp = Fingerprint(command with { AdminPin = "" });
        var prior = await db.ProductionScheduleRevisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null)
            return prior.RequestFingerprint == fp
                ? new(ProductionDailyCommandStatus.Success, command.WeekId)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);
        var pinUser = await pins.AuthenticateAsync(command.AdminPin, token);
        if (pinUser?.Role.Code != "ADMIN" || pinUser.Id != command.ActorUserId)
            return new(ProductionDailyCommandStatus.InvalidPin, Errors: ["El NIP ADMIN no corresponde a la sesión actual."]);

        var week = await db.ProductionScheduleWeeks.Include(x => x.Lines).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
            .SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
        if (week is null) return new(ProductionDailyCommandStatus.NotFound);
        if (week.Version != command.ExpectedVersion) return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        if (week.Status != ProductionScheduleWeekStatus.Draft) return Invalid("Sólo una semana en borrador puede publicarse.");

        var validation = await ValidateForPublicationAsync(week, token);
        if (validation.Count > 0) return new(ProductionDailyCommandStatus.ValidationFailed, Errors: validation);

        var ownsTransaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token)
            : null;
        try
        {
            var traceability = new ProductionTraceabilityService(db, pins,
                new ProductionMaterialService(db, pins, movements, timeProvider), timeProvider);
            foreach (var line in week.Lines.Where(x => !x.IsCancelled).OrderBy(x => x.Sequence))
            {
                var reference = string.Join(" / ", new[] { line.OrderReference1, line.OrderReference2, line.OrderReference3 }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                var order = await BuildDailyOrderAsync(Derive(command.OperationId, line.Id, "create"), line.Product,
                    line.Quantity, string.IsNullOrEmpty(reference) ? null : reference, line.PlannedDate, line.Notes,
                    line.IsCarryover ? line.StartArea : null, pinUser.Id, "Orden creada al publicar el programa semanal.", token);
                var released = await ReleaseDailyOrderAsync(order, Derive(command.OperationId, line.Id, "release"), pinUser.Id,
                    line.IsCarryover ? "Publicación de arrastre del programa semanal" : "Publicación del programa semanal", token);
                if (released.Status != ProductionCommandStatus.Success)
                    return await AbortAsync(transaction, Map(released, $"Línea {line.Sequence}"), token);
                await db.Entry(order).ReloadAsync(token);
                var batch = await traceability.CreateBatchAsync(new CreateProductionBatchCommand(
                    Derive(command.OperationId, line.Id, "batch"), order.Id, line.Quantity, order.Version,
                    command.AdminPin), token);
                if (!batch.Success)
                    return await AbortAsync(transaction, new(ProductionDailyCommandStatus.ValidationFailed,
                        Errors: Prefix($"Línea {line.Sequence}", batch.Errors)), token);
                await db.Entry(order).ReloadAsync(token);
                line.WorkOrderId = order.Id;
                line.Version++;
            }

            week.Status = ProductionScheduleWeekStatus.Open;
            week.PublishedByUserId = command.ActorUserId;
            week.PublishedAt = timeProvider.GetUtcNow();
            week.Version++;
            db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fp, week.Id, null,
                "published", JsonSerializer.Serialize(new { Status = ProductionScheduleWeekStatus.Draft }),
                JsonSerializer.Serialize(new { Status = week.Status, Orders = week.Lines.Count }), command.ActorUserId));
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, week.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            throw;
        }
    }

    public Task<ProductionDailyCommandResult> CloseAsync(ChangeProductionScheduleWeekStatusCommand command,
        CancellationToken token = default) => ChangeStatusAsync(command, ProductionScheduleWeekStatus.Open,
        ProductionScheduleWeekStatus.Closed, "closed", token);

    public Task<ProductionDailyCommandResult> ReopenAsync(ChangeProductionScheduleWeekStatusCommand command,
        CancellationToken token = default) => ChangeStatusAsync(command, ProductionScheduleWeekStatus.Closed,
        ProductionScheduleWeekStatus.Open, "reopened", token);

    private async Task<ProductionDailyCommandResult> ReplacePublishedOrderAsync(
        ProductionScheduleLine scheduleLine, Product product, SaveProductionScheduleLineCommand command,
        CancellationToken token)
    {
        var oldOrder = await db.ProductionWorkOrders
            .Include(x => x.Events)
            .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.Reservations)
            .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.IssueLinks)
            .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.Preparations)
            .SingleAsync(x => x.Id == scheduleLine.WorkOrderId, token);
        if (oldOrder.Events.Any(x => x.Type is ProductionEventType.Processed or ProductionEventType.Reworked or
                ProductionEventType.Delivered or ProductionEventType.Received or ProductionEventType.WarehouseReceived) ||
            oldOrder.SupplyRequests.SelectMany(x => x.Lines).SelectMany(x => x.IssueLinks)
                .Any(x => x.Quantity > x.CancelledQuantity))
            return Invalid("El SKU ya tiene movimientos. Corrige la orden desde Producción avanzada.");

        var now = timeProvider.GetUtcNow();
        const string reason = "Sustitución de SKU desde el programa semanal antes de iniciar producción.";
        foreach (var request in oldOrder.SupplyRequests)
        {
            foreach (var supplyLine in request.Lines)
            {
                var delivered = supplyLine.IssueLinks.Sum(x => x.Quantity - x.CancelledQuantity);
                var pending = Math.Max(0, supplyLine.RequiredQuantity - supplyLine.CancelledQuantity +
                                           supplyLine.ReopenedQuantity - delivered);
                if (pending > 0)
                {
                    supplyLine.CancelledQuantity += pending;
                    foreach (var reservation in supplyLine.Reservations.Where(x => x.Quantity > x.ReleasedQuantity))
                        reservation.ReleasedQuantity = reservation.Quantity;
                    db.ProductionSupplyEvents.Add(new ProductionSupplyEvent
                    {
                        OperationId = Derive(command.OperationId, supplyLine.Id, "replace-cancel-supply"),
                        RequestFingerprint = Fingerprint(new
                        { ScheduleLineId = scheduleLine.Id, SupplyLineId = supplyLine.Id, pending, reason }),
                        SupplyRequestId = request.Id,
                        SupplyRequestLineId = supplyLine.Id,
                        Type = ProductionSupplyEventType.QuantityCancelled,
                        ResponsibleUserId = command.ActorUserId,
                        Quantity = pending,
                        Reason = reason,
                        RecordedAt = now
                    });
                }
                foreach (var preparation in supplyLine.Preparations
                             .Where(x => x.Status == ProductionSupplyPreparationStatus.Open))
                {
                    preparation.Status = ProductionSupplyPreparationStatus.Discarded;
                    preparation.UpdatedAt = now;
                }
            }
            ProductionSupplyService.UpdateStatus(request);
            request.Version++;
        }
        oldOrder.Status = ProductionWorkOrderStatus.Cancelled;
        var cancellationEvent = new ProductionEvent
        {
            OperationId = Derive(command.OperationId, scheduleLine.Id, "replace-cancel-order"),
            RequestFingerprint = Fingerprint(new
            { ScheduleLineId = scheduleLine.Id, WorkOrderId = oldOrder.Id, reason }),
            WorkOrderId = oldOrder.Id,
            Type = ProductionEventType.Cancelled,
            ResponsibleUserId = command.ActorUserId,
            Reason = reason,
            RecordedAt = now
        };
        oldOrder.Events.Add(cancellationEvent);
        db.Entry(cancellationEvent).State = EntityState.Added;
        oldOrder.Version++;

        var reference = string.Join(" / ", new[]
            { command.OrderReference1, command.OrderReference2, command.OrderReference3 }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        var order = await BuildDailyOrderAsync(Derive(command.OperationId, scheduleLine.Id, "replacement-create"), product,
            command.Quantity, string.IsNullOrEmpty(reference) ? null : reference, command.PlannedDate, command.Notes,
            scheduleLine.IsCarryover ? scheduleLine.StartArea : null, command.ActorUserId,
            "Orden creada por sustitución en el programa semanal.", token);
        order.Status = ProductionWorkOrderStatus.Released;
        order.ReleasedAt = now;
        await ProductionSupplyService.GenerateForReleaseAsync(db, order, now, token);
        var releaseOperation = Derive(command.OperationId, scheduleLine.Id, "replacement-release");
        var releasedEvent = new ProductionEvent
        {
            OperationId = releaseOperation,
            RequestFingerprint = Fingerprint(new { order.Id, order.Version, reason }),
            WorkOrderId = order.Id,
            Type = ProductionEventType.Released,
            ResponsibleUserId = command.ActorUserId,
            Reason = reason,
            RecordedAt = now
        };
        order.Events.Add(releasedEvent);
        db.Entry(releasedEvent).State = EntityState.Added;
        order.Version++;
        await db.SaveChangesAsync(token);

        var batchOperation = Derive(command.OperationId, scheduleLine.Id, "replacement-batch");
        var batchNumber = $"{order.Number}-L001";
        var lot = new ProductLot
        {
            ProductId = order.ProductId,
            Number = batchNumber,
            NormalizedNumber = batchNumber.ToUpperInvariant(),
            CreatedAt = now
        };
        db.ProductionBatches.Add(new ProductionBatch
        {
            CreateOperationId = batchOperation,
            CreateFingerprint = Fingerprint(new CreateProductionBatchCommand(batchOperation, order.Id,
                command.Quantity, order.Version, string.Empty)),
            WorkOrderId = order.Id,
            Number = batchNumber,
            AssignedQuantity = order.AuthorizedQuantity,
            FinishedProductLot = lot,
            CreatedByUserId = command.ActorUserId,
            CreatedAt = now
        });
        order.Version++;
        await db.SaveChangesAsync(token);
        return new(ProductionDailyCommandStatus.Success, order.Id);
    }

    // The daily program sets the process: orders follow Corte, Costura and Ready to Pack from the daily configuration,
    // starting at the carryover area; published orders keep their saved stages.
    // They never snapshot a recipe, so daily captures do not require material supply.
    private async Task<ProductionWorkOrder> BuildDailyOrderAsync(Guid operationId, Product product, decimal quantity,
        string? reference, DateOnly dueDate, string? notes, ProductionDailyArea? startArea, Guid actorId, string reason,
        CancellationToken token)
    {
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var chosen = ProductionDailyProcessFlow.Resolve(config, [], startArea);
        var stageIds = chosen.Select(area => Stage(config, area)).ToArray();
        var stages = await db.ProductionStages.AsNoTracking().Where(x => stageIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        var now = timeProvider.GetUtcNow();
        var fingerprint = Fingerprint(new CreateProductionOrderCommand(operationId, product.Id, quantity, reference, dueDate,
            notes, string.Empty));
        var orderId = Guid.NewGuid();
        var order = new ProductionWorkOrder
        {
            Id = orderId,
            CreateOperationId = operationId,
            CreateFingerprint = fingerprint,
            Number = $"OT-{now:yyyy}-{orderId.ToString("N")[..6].ToUpperInvariant()}",
            ExternalReference = Trim(reference, 120),
            ProductId = product.Id,
            UnitId = product.BaseUnitId,
            OriginalTargetQuantity = quantity,
            TargetQuantity = quantity,
            AuthorizedQuantity = quantity,
            DueDate = dueDate,
            Notes = Trim(notes, 500),
            CreatedByUserId = actorId,
            CreatedAt = now,
            UsesBatchTraceability = true
        };
        for (var index = 0; index < stageIds.Length; index++)
            order.Stages.Add(new ProductionWorkOrderStage
            {
                SourceStageId = stageIds[index],
                Sequence = index + 1,
                Code = stages[stageIds[index]].Code,
                Name = stages[stageIds[index]].Name
            });
        order.Events.Add(new ProductionEvent
        {
            OperationId = operationId,
            RequestFingerprint = fingerprint,
            WorkOrderId = order.Id,
            Type = ProductionEventType.Created,
            ResponsibleUserId = actorId,
            Reason = reason,
            RecordedAt = now
        });
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync(token);
        return order;
    }

    // Daily orders carry no material plan, so the advanced release validation (recipe, route snapshot) does not apply.
    private async Task<ProductionCommandResult> ReleaseDailyOrderAsync(ProductionWorkOrder order,
        Guid operationId, Guid actorId, string reason, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow();
        order.Status = ProductionWorkOrderStatus.Released;
        order.ReleasedAt = now;
        await ProductionSupplyService.GenerateForReleaseAsync(db, order, now, token);
        var releaseCommand = new ProductionOrderActionCommand(operationId, order.Id, order.Version,
            string.Empty, reason);
        var releaseEvent = new ProductionEvent
        {
            OperationId = operationId,
            RequestFingerprint = Fingerprint(releaseCommand with { Pin = string.Empty }),
            WorkOrderId = order.Id,
            Type = ProductionEventType.Released,
            ResponsibleUserId = actorId,
            Reason = reason,
            RecordedAt = now
        };
        order.Events.Add(releaseEvent);
        db.Entry(releaseEvent).State = EntityState.Added;
        order.Version++;
        await db.SaveChangesAsync(token);
        return new(ProductionCommandStatus.Success, order.Id);
    }

    private async Task<ProductionDailyCommandResult> ChangeStatusAsync(ChangeProductionScheduleWeekStatusCommand command,
        ProductionScheduleWeekStatus expected, ProductionScheduleWeekStatus next, string action,
        CancellationToken token)
    {
        var fp = Fingerprint(command);
        var prior = await db.ProductionScheduleRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.RequestFingerprint == fp
            ? new(ProductionDailyCommandStatus.Success, command.WeekId)
            : new(ProductionDailyCommandStatus.IdempotencyConflict);
        if (!await IsAdminAsync(command.ActorUserId, token)) return Invalid("La operación requiere ADMIN.");
        var week = await db.ProductionScheduleWeeks.SingleOrDefaultAsync(x => x.Id == command.WeekId, token);
        if (week is null) return new(ProductionDailyCommandStatus.NotFound);
        if (week.Version != command.ExpectedVersion) return new(ProductionDailyCommandStatus.ConcurrencyConflict);
        if (week.Status != expected) return Invalid($"La semana debe estar en estado {expected}.");
        var before = week.Status;
        week.Status = next;
        week.Version++;
        if (next == ProductionScheduleWeekStatus.Closed)
        {
            week.ClosedByUserId = command.ActorUserId;
            week.ClosedAt = timeProvider.GetUtcNow();
        }
        else
        {
            week.ClosedByUserId = null;
            week.ClosedAt = null;
        }
        db.ProductionScheduleRevisions.Add(Revision(command.OperationId, fp, week.Id, null, action,
            JsonSerializer.Serialize(new { Status = before }), JsonSerializer.Serialize(new { Status = next }), command.ActorUserId));
        await db.SaveChangesAsync(token);
        return new(ProductionDailyCommandStatus.Success, week.Id);
    }

    private async Task<List<string>> ValidateForPublicationAsync(ProductionScheduleWeek week, CancellationToken token)
    {
        var errors = new List<string>();
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var allowed = new[] { config.CuttingStageId, config.SewingStageId, config.ReadyToPackStageId };
        if (allowed.Any(x => !x.HasValue) || config.Shift1Id is null || config.Shift2Id is null)
        {
            errors.Add("Configura Corte, Costura, Ready to Pack, T1 y T2 antes de publicar.");
            return errors;
        }
        // Routes and recipes are optional for the daily program; only its own processes and shifts must be usable.
        var stageIds = allowed.Select(x => x!.Value).ToArray();
        var shiftIds = new[] { config.Shift1Id.Value, config.Shift2Id.Value };
        if (stageIds.Distinct().Count() != 3 ||
            await db.ProductionStages.CountAsync(x => stageIds.Contains(x.Id) && x.IsActive, token) != 3)
            errors.Add("Selecciona tres procesos activos y diferentes.");
        if (await db.ProductionShifts.CountAsync(x => shiftIds.Contains(x.Id) && x.IsActive, token) != 2)
            errors.Add("Selecciona T1 y T2 entre los turnos activos.");
        foreach (var line in week.Lines.Where(x => !x.IsCancelled && !x.Product.IsActive).OrderBy(x => x.Sequence))
            errors.Add($"Línea {line.Sequence} ({line.Product.Sku}): Selecciona un SKU activo del catálogo.");
        return errors.Distinct().ToList();
    }

    private IQueryable<ProductionScheduleWeek> WeekQuery() => db.ProductionScheduleWeeks.AsNoTracking()
        .Include(x => x.Lines).ThenInclude(x => x.Product);

    private static ProductionScheduleWeekView View(ProductionScheduleWeek week) => new(
        week.Id, week.WeekStart, week.WeekEnd, week.Status, week.Origin, week.Version,
        week.Lines.Where(x => !x.IsCancelled && !x.IsExtra).OrderBy(x => x.Sequence).Select(x => new ProductionScheduleLineView(
            x.Id, x.Sequence, x.PlannedDate, x.ProductId, x.Product.Sku, x.Product.Description, x.Quantity,
            x.OrderReference1, x.OrderReference2, x.OrderReference3, x.Notes, x.IsCarryover,
            x.StartArea, x.WorkOrderId, x.Version)).ToArray());

    private static ProductionDailyConfigurationView View(ProductionDailyConfiguration row) => new(
        row.CuttingStageId, row.SewingStageId, row.ReadyToPackStageId, row.Shift1Id, row.Shift2Id,
        row.Version, row.CuttingStageId.HasValue && row.SewingStageId.HasValue && row.ReadyToPackStageId.HasValue &&
                     row.Shift1Id.HasValue && row.Shift2Id.HasValue);

    private async Task<bool> IsAdminAsync(Guid userId, CancellationToken token) =>
        await db.Users.AnyAsync(x => x.Id == userId && x.IsActive && x.Role.Code == "ADMIN", token);

    private async Task<decimal> EffectiveGoodAsync(Guid orderId, CancellationToken token)
    {
        var events = await db.ProductionEvents.AsNoTracking().Where(x => x.WorkOrderId == orderId).ToListAsync(token);
        var reversed = events.Where(x => x.Type == ProductionEventType.ResultReversed && x.RelatedEventId.HasValue)
            .Select(x => x.RelatedEventId!.Value).ToHashSet();
        // Every stage processes the same pieces, so the line has used as many as its busiest stage, not their sum.
        return events.Where(x => !reversed.Contains(x.Id) && x.Type is ProductionEventType.Processed or ProductionEventType.Reworked)
            .GroupBy(x => x.WorkOrderStageId).Select(x => x.Sum(y => y.GoodQuantity)).DefaultIfEmpty(0).Max();
    }

    private ProductionScheduleRevision Revision(Guid operationId, string fp, Guid weekId, Guid? lineId,
        string action, string before, string after, Guid actorId) => new()
        {
            OperationId = operationId,
            RequestFingerprint = fp,
            WeekId = weekId,
            LineId = lineId,
            Action = action,
            BeforeJson = before,
            AfterJson = after,
            ResponsibleUserId = actorId,
            RecordedAt = timeProvider.GetUtcNow()
        };

    private static object LineSnapshot(ProductionScheduleLine line) => new
    {
        line.Id,
        line.Sequence,
        line.PlannedDate,
        line.ProductId,
        line.Quantity,
        line.OrderReference1,
        line.OrderReference2,
        line.OrderReference3,
        line.Notes,
        line.IsCarryover,
        line.StartArea,
        line.WorkOrderId,
        line.IsCancelled,
        line.Version
    };

    private static Guid Stage(ProductionDailyConfiguration config, ProductionDailyArea area) => area switch
    {
        ProductionDailyArea.Cutting => config.CuttingStageId!.Value,
        ProductionDailyArea.Sewing => config.SewingStageId!.Value,
        _ => config.ReadyToPackStageId!.Value
    };

    private static Guid Derive(Guid operationId, Guid entityId, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId:N}:{entityId:N}:{purpose}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static ProductionDailyCommandResult Map(ProductionCommandResult result, string prefix) => result.Status switch
    {
        ProductionCommandStatus.InvalidPin => new(ProductionDailyCommandStatus.InvalidPin, Errors: Prefix(prefix, result.Errors)),
        ProductionCommandStatus.ConcurrencyConflict => new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: Prefix(prefix, result.Errors)),
        ProductionCommandStatus.IdempotencyConflict => new(ProductionDailyCommandStatus.IdempotencyConflict, Errors: Prefix(prefix, result.Errors)),
        _ => new(ProductionDailyCommandStatus.ValidationFailed, Errors: Prefix(prefix, result.Errors))
    };

    private static IReadOnlyList<string> Prefix(string prefix, IReadOnlyList<string>? errors) =>
        errors is { Count: > 0 } ? errors.Select(x => $"{prefix}: {x}").ToArray() : [$"{prefix}: no fue posible completar la operación."];

    private static async Task<ProductionDailyCommandResult> AbortAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx,
        ProductionDailyCommandResult result, CancellationToken token)
    {
        if (tx is not null) await tx.RollbackAsync(token);
        return result;
    }

    private static ProductionDailyCommandResult Invalid(string error) =>
        new(ProductionDailyCommandStatus.ValidationFailed, Errors: [error]);
    private static string? Trim(string? value, int max)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return value?.Length > max ? value[..max] : value;
    }

    private static string Fingerprint<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}



