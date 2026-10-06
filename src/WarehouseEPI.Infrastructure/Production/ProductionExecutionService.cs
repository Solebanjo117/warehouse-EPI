using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ExecutionMaterial(Guid PlanId, decimal Quantity);
public sealed record ExecutionRetention(Guid CaseId, Guid? IssueLinkId, Guid? ReservationId, decimal Quantity);
public sealed record ProductionExecutionCommand(Guid OperationId, Guid WorkOrderId, uint Version, string Action,
    string Pin, Guid ReasonId, string? Comment = null, string? AdminPin = null,
    decimal Target = 0, decimal Authorized = 0, DateOnly? DueDate = null,
    IReadOnlyList<ExecutionMaterial>? Materials = null, IReadOnlyList<ExecutionRetention>? Retentions = null,
    Guid? CaseId = null, Guid? PlanId = null, decimal Quantity = 0);
public sealed record ReworkView(Guid Id, Guid BatchId, Guid StageId, string Stage, decimal Initial,
    decimal Pending, decimal Recovered, decimal Discarded, int Attempts, DateTimeOffset OriginAt, bool NeedsAttention = true);

public sealed class ProductionExecutionService(WarehouseDbContext db, UserPinService pins, TimeProvider clock)
{
    public async Task<IReadOnlyList<ReworkView>> GetReworkAsync(Guid orderId, CancellationToken token = default)
    {
        var reversed = await ReversedAsync(orderId, token);
        var cases = await db.ProductionReworkCases.AsNoTracking().Include(x => x.OriginResult)
            .Include(x => x.WorkOrderStage).Include(x => x.Attempts).ThenInclude(x => x.Result)
            .Where(x => x.WorkOrderId == orderId).OrderBy(x => x.OriginAt).ToListAsync(token);
        var order = await db.ProductionWorkOrders.AsNoTracking().Include(x => x.Stages).Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == orderId, token);
        var downstreamPending = order is not null && ProductionService.BuildProgress(order).Any(x => x.AvailableInput > 0 || x.AvailableToDeliver > 0 || x.PendingReceipt > 0);
        return cases.Where(x => !reversed.Contains(x.OriginResult.OperationId)).Select(x =>
        {
            var attempts = x.Attempts.Where(a => !reversed.Contains(a.Result.OperationId)).ToArray();
            var good = attempts.Sum(a => a.Result.GoodQuantity);
            var scrap = attempts.Sum(a => a.Result.ScrapQuantity);
            return new ReworkView(x.Id, x.BatchId, x.WorkOrderStageId, x.WorkOrderStage.Name,
                x.InitialQuantity, x.InitialQuantity - good - scrap, good, scrap, attempts.Length, x.OriginAt,
                x.InitialQuantity > good + scrap || (good > 0 && downstreamPending));
        }).ToArray();
    }

    internal async Task<HashSet<Guid>> ReversedAsync(Guid orderId, CancellationToken token) =>
        (await db.ProductionEvents.Where(x => x.WorkOrderId == orderId && x.Type == ProductionEventType.ResultReversed && x.RelatedEventId != null)
            .Select(x => x.RelatedEvent!.OperationId).ToListAsync(token)).ToHashSet();

    internal static async Task<bool> ReworkIsOpenAsync(WarehouseDbContext db, Guid caseId, CancellationToken token)
    {
        var item = await db.ProductionReworkCases.AsNoTracking().Include(x => x.OriginResult)
            .Include(x => x.Attempts).ThenInclude(x => x.Result).SingleOrDefaultAsync(x => x.Id == caseId, token);
        if (item is null) return false;
        var reversed = await db.ProductionEvents.Where(x => x.WorkOrderId == item.WorkOrderId && x.Type == ProductionEventType.ResultReversed && x.RelatedEventId != null)
            .Select(x => x.RelatedEvent!.OperationId).ToListAsync(token);
        if (reversed.Contains(item.OriginResult.OperationId)) return false;
        var effective = item.Attempts.Where(x => !reversed.Contains(x.Result.OperationId)).ToArray();
        if (item.InitialQuantity > effective.Sum(x => x.Result.GoodQuantity + x.Result.ScrapQuantity)) return true;
        if (effective.Sum(x => x.Result.GoodQuantity) == 0) return false;
        var order = await db.ProductionWorkOrders.AsNoTracking().Include(x => x.Stages).Include(x => x.Events).SingleAsync(x => x.Id == item.WorkOrderId, token);
        return order.Status != ProductionWorkOrderStatus.Closed && ProductionService.BuildProgress(order).Any(x => x.AvailableInput > 0 || x.AvailableToDeliver > 0 || x.PendingReceipt > 0);
    }

    public async Task<string?> ResolveReasonAsync(Guid id, ProductionReasonCategory category, string? comment, CancellationToken token)
    {
        var reason = await db.ProductionReasons.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsActive && x.Category == category, token);
        if (reason is null || (reason.RequiresComment && string.IsNullOrWhiteSpace(comment)) || comment?.Length > 300) return null;
        var snapshot = $"{reason.Code}: {reason.Description}" + (string.IsNullOrWhiteSpace(comment) ? "" : $" — {comment.Trim()}");
        return snapshot.Length <= 500 ? snapshot : null;
    }

    public Task<ProductionCommandResult> ApplyAsync(ProductionExecutionCommand command, CancellationToken token = default) =>
        ApplyCoreAsync(command, null, token);

    internal Task<ProductionCommandResult> ApplyAuthorizedAsync(ProductionExecutionCommand command,
        Guid authenticatedAdminUserId, CancellationToken token = default) =>
        ApplyCoreAsync(command, authenticatedAdminUserId, token);

    private async Task<ProductionCommandResult> ApplyCoreAsync(ProductionExecutionCommand command,
        Guid? authenticatedAdminUserId, CancellationToken token)
    {
        var user = authenticatedAdminUserId.HasValue
            ? await db.Users.Include(x => x.Role).SingleOrDefaultAsync(x => x.Id == authenticatedAdminUserId && x.IsActive, token)
            : await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code is not ("ADMIN" or "OPERATOR")) return new(ProductionCommandStatus.InvalidPin);
        var administrator = user.Role.Code == "ADMIN" ? user : string.IsNullOrEmpty(command.AdminPin) ? null : await pins.AuthenticateAsync(command.AdminPin, token);
        if (administrator?.Role.Code != "ADMIN") administrator = null;
        if (command.Action != "principal" && user.Role.Code != "ADMIN") return new(ProductionCommandStatus.InvalidPin);
        if (command.OperationId == Guid.Empty) return Invalid("Identificador de operación inválido.");
        var fingerprint = Hash(new { Command = command with { Pin = "", AdminPin = null }, User = user.Id, Admin = administrator?.Id });
        var prior = await db.ProductionExecutionAudits.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.WorkOrderId == command.WorkOrderId && prior.Fingerprint == fingerprint
            ? new(ProductionCommandStatus.Success, command.WorkOrderId) : new(ProductionCommandStatus.IdempotencyConflict);
        await using var tx = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var order = await db.ProductionWorkOrders.Include(x => x.Unit).Include(x => x.Stages).Include(x => x.Events)
                .Include(x => x.Batches).ThenInclude(x => x.Results).Include(x => x.MaterialPlan).ThenInclude(x => x.Unit)
                .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.Reservations)
                .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.IssueLinks)
                .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.Preparations)
                .SingleOrDefaultAsync(x => x.Id == command.WorkOrderId, token);
            if (order is null || order.Version != command.Version) return await Fail("La orden cambió. Recarga antes de confirmar.");
            if (!ProductionActionPolicy.Allows(order.Status, command.Action)) return await Fail("La acción no está disponible para el estado actual de la orden.");
            var category = command.Action is "adjust" or "reopen" or "retain" or "request" ? ProductionReasonCategory.Adjustment : ProductionReasonCategory.Difference;
            var reason = await ResolveReasonAsync(command.ReasonId, category, command.Comment, token);
            if (reason is null) return await Fail("Selecciona un motivo activo y completa el comentario requerido.");
            var before = await SnapshotAsync(order, token);
            string? error = command.Action switch
            {
                "adjust" => await AdjustAsync(order, command, token),
                "retain" => await RetainAsync(order, command, token),
                "request" => await RequestAsync(order, command, token),
                "principal" => await CloseAsync(order, false, administrator, token),
                "definitive" => await CloseAsync(order, true, administrator, token),
                "reopen" => Reopen(order),
                _ => "Acción no válida."
            };
            if (error is not null) return await Fail(error);
            order.Version++;
            db.ProductionExecutionAudits.Add(new()
            {
                OperationId = command.OperationId,
                WorkOrderId = order.Id,
                Fingerprint = fingerprint,
                Action = command.Action,
                ResponsibleUserId = user.Id,
                AuthorizedByUserId = administrator?.Id,
                Reason = reason,
                BeforeJson = before,
                AfterJson = JsonSerializer.Serialize(new { State = await SnapshotAsync(order, token), Input = command with { Pin = "", AdminPin = null } }),
                RecordedAt = clock.GetUtcNow()
            });
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(ProductionCommandStatus.Success, order.Id);
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            prior = await db.ProductionExecutionAudits.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            return prior?.Fingerprint == fingerprint ? new(ProductionCommandStatus.Success, command.WorkOrderId) : new(ProductionCommandStatus.ConcurrencyConflict);
        }
        async Task<ProductionCommandResult> Fail(string message)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return Invalid(message);
        }
    }

    private async Task<string?> CloseAsync(ProductionWorkOrder order, bool definitive, User? admin, CancellationToken token)
    {
        if (definitive ? order.Status != ProductionWorkOrderStatus.PrincipalClosed : order.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress))
            return "La orden no admite este cierre en su estado actual.";
        var progress = ProductionService.BuildProgress(order);
        if (ProductionActionPolicy.ClosureProgress(progress).Count > 0)
            return "Concilia cantidades por procesar, entregas y producto bueno antes de cerrar. ADMIN debe ajustar la meta si ya no se fabricará lo pendiente.";
        var rework = await GetReworkAsync(order.Id, token);
        if (!order.UsesBatchTraceability && progress.Any(x => x.Rework > 0)) return "El retrabajo histórico sin lote debe conciliarse antes del cierre principal.";
        if (definitive && (rework.Any(x => x.Pending > 0) || progress.Any(x => x.Rework > 0))) return "Resuelve todo el retrabajo antes del cierre definitivo.";
        var received = order.Events.Where(x => x.Type == ProductionEventType.WarehouseReceived).Sum(x => x.Quantity);
        if (admin is null && received != order.TargetQuantity) return "La diferencia requiere NIP ADMIN para esta versión y cantidades.";
        var lines = order.SupplyRequests.SelectMany(x => x.Lines).ToArray();
        if (lines.Any(x => Pending(x) > 0 && (definitive || x.ReworkCaseId is null))) return "Concilia las solicitudes de surtimiento pendientes.";
        var remaining = await RemainingWipAsync(order.Id, token);
        var retained = await db.ProductionReworkRetentions.Where(x => x.ReworkCase.WorkOrderId == order.Id).ToListAsync(token);
        var open = rework.Where(x => x.Pending > 0).Select(x => x.Id).ToHashSet();
        if (remaining.Any(x => x.Value > 0 && (definitive || !retained.Any(r => r.IssueLinkId == x.Key && r.Quantity >= x.Value && open.Contains(r.ReworkCaseId)))))
            return "Devuelve los sobrantes WIP o registra su retención ADMIN para retrabajo.";
        foreach (var line in lines)
            foreach (var reservation in line.Reservations.Where(x => x.Quantity > x.ReleasedQuantity))
                if (definitive || !retained.Any(r => r.WarehouseReservationId == reservation.Id && open.Contains(r.ReworkCaseId) && r.Quantity >= reservation.Quantity - reservation.ReleasedQuantity))
                    return "Libera o retén explícitamente las reservas de almacén antes de cerrar.";
        order.Status = definitive ? ProductionWorkOrderStatus.Closed : ProductionWorkOrderStatus.PrincipalClosed;
        if (definitive) order.ClosedAt = clock.GetUtcNow(); else order.PrincipalClosedAt = clock.GetUtcNow();
        return null;
    }

    private static string? Reopen(ProductionWorkOrder order)
    {
        if (order.Status is not (ProductionWorkOrderStatus.PrincipalClosed or ProductionWorkOrderStatus.Closed)) return "Sólo se reabre una orden cerrada.";
        order.Status = ProductionWorkOrderStatus.InProgress; order.ClosedAt = null; order.PrincipalClosedAt = null;
        return null;
    }

    private async Task<string?> AdjustAsync(ProductionWorkOrder order, ProductionExecutionCommand command, CancellationToken token)
    {
        if (order.Status is not (ProductionWorkOrderStatus.Draft or ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress or ProductionWorkOrderStatus.Paused)) return "Reabre la fabricación principal antes de ajustar su plan.";
        bool Valid(decimal value, bool decimals) => value > 0 && decimal.Round(value, 4) == value && (decimals || decimal.Truncate(value) == value);
        if (!Valid(command.Target, order.Unit.AllowsDecimals) || !Valid(command.Authorized, order.Unit.AllowsDecimals)) return "Meta y cantidad autorizada deben ser positivas y compatibles con su unidad.";
        var reversed = await ReversedAsync(order.Id, token);
        var processed = order.Events.Where(x => x.Type == ProductionEventType.Processed && !reversed.Contains(x.OperationId))
            .GroupBy(x => x.WorkOrderStageId).Select(x => x.Sum(e => e.Quantity)).DefaultIfEmpty().Max();
        var received = order.Events.Where(x => x.Type == ProductionEventType.WarehouseReceived).Sum(x => x.Quantity);
        if (Math.Min(command.Target, command.Authorized) < Math.Max(processed, received)) return "No puedes reducir por debajo de lo procesado o recibido. Corrige primero las operaciones erróneas.";
        var inputs = command.Materials ?? [];
        if (inputs.Count != order.MaterialPlan.Count || inputs.Select(x => x.PlanId).Distinct().Count() != inputs.Count) return "Confirma las cantidades de todos los materiales del plan.";
        var reversedMaterial = await db.ProductionMaterialOperations.Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var used = await db.ProductionMaterialOperationLines.Include(x => x.Operation).Include(x => x.IssueLink)
            .Where(x => x.Operation.WorkOrderId == order.Id && (x.Operation.Type == ProductionMaterialOperationType.Consumption || x.Operation.Type == ProductionMaterialOperationType.Scrap) && !reversedMaterial.Contains(x.Operation.Id)).ToListAsync(token);
        foreach (var input in inputs)
        {
            var plan = order.MaterialPlan.SingleOrDefault(x => x.Id == input.PlanId);
            if (plan is null || !Valid(input.Quantity, plan.Unit.AllowsDecimals)) return "Una cantidad de material no es válida.";
            if (input.Quantity < used.Where(x => x.IssueLink.ProductId == plan.MaterialProductId && x.IssueLink.WorkOrderStageId == plan.WorkOrderStageId).Sum(x => x.Quantity)) return "El plan no puede ser menor que lo consumido o descartado.";
            var lines = order.SupplyRequests.SelectMany(x => x.Lines).Where(x => x.MaterialPlanId == plan.Id && x.ReworkCaseId == null).ToArray();
            if (lines.Length > 1) return "El plan tiene solicitudes históricas múltiples; requiere conciliación antes del ajuste.";
            if (lines.SingleOrDefault() is { } line)
            {
                var delta = input.Quantity - plan.PlannedQuantity;
                if (delta >= 0) line.RequiredQuantity += delta;
                else
                {
                    var cancel = Math.Min(-delta, Pending(line));
                    line.CancelledQuantity += cancel;
                    Release(line, cancel);
                }
                Invalidate(line);
            }
            else if (order.UsesSupplyRequests && input.Quantity > plan.PlannedQuantity)
            {
                var destinationCode = plan.WipTargetCode ?? "WIP por seleccionar";
                var request = order.SupplyRequests.SingleOrDefault(x => x.WorkOrderStageId == plan.WorkOrderStageId && x.DestinationCode == destinationCode);
                if (request is null)
                {
                    request = new()
                    {
                        WorkOrder = order,
                        WorkOrderStageId = plan.WorkOrderStageId,
                        DestinationCode = destinationCode,
                        DestinationLocationId = plan.WipLocationId,
                        CreatedAt = clock.GetUtcNow()
                    };
                    db.ProductionSupplyRequests.Add(request);
                }
                db.ProductionSupplyRequestLines.Add(new()
                {
                    SupplyRequest = request,
                    MaterialPlanId = plan.Id,
                    ProductId = plan.MaterialProductId,
                    UnitId = plan.UnitId,
                    RequiredQuantity = input.Quantity - plan.PlannedQuantity
                });
                request.Status = ProductionSupplyRequestStatus.InProgress; request.Version++;
            }
            plan.PlannedQuantity = input.Quantity;
        }
        if (order.OriginalTargetQuantity == 0) order.OriginalTargetQuantity = order.TargetQuantity;
        order.TargetQuantity = command.Target; order.AuthorizedQuantity = command.Authorized; order.DueDate = command.DueDate;
        if (order.Batches.Count == 1) order.Batches.Single().AssignedQuantity = command.Authorized;
        else if (order.Batches.Sum(x => x.AssignedQuantity) > command.Authorized) return "La cantidad no cubre los lotes históricos existentes.";
        return null;
    }

    private async Task<string?> RetainAsync(ProductionWorkOrder order, ProductionExecutionCommand command, CancellationToken token)
    {
        if (order.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress)) return "Selecciona las reservas antes del cierre principal.";
        var cases = (await GetReworkAsync(order.Id, token)).Where(x => x.NeedsAttention).ToDictionary(x => x.Id);
        var selections = command.Retentions ?? [];
        if (selections.Any(x => x.Quantity <= 0 || decimal.Round(x.Quantity, 4) != x.Quantity || !cases.ContainsKey(x.CaseId) || x.IssueLinkId.HasValue == x.ReservationId.HasValue) ||
            selections.Select(x => x.IssueLinkId ?? x.ReservationId).Distinct().Count() != selections.Count) return "Selecciona cantidades válidas y un único retrabajo para cada reserva.";
        var remaining = await RemainingWipAsync(order.Id, token);
        var links = await db.ProductionMaterialIssueLinks.Include(x => x.Product).ThenInclude(x => x.BaseUnit).Where(x => x.WorkOrderId == order.Id).ToListAsync(token);
        var reservations = order.SupplyRequests.SelectMany(x => x.Lines).SelectMany(x => x.Reservations).ToArray();
        foreach (var item in selections)
        {
            if (item.IssueLinkId is Guid linkId && (!remaining.TryGetValue(linkId, out var available) || available < item.Quantity || order.Stages.Single(x => x.Id == links.Single(x => x.Id == linkId).WorkOrderStageId).Sequence < order.Stages.Single(x => x.Id == cases[item.CaseId].StageId).Sequence)) return "La reserva WIP no corresponde al proceso o excede lo disponible.";
            if (item.IssueLinkId is Guid quantityLinkId && !links.Single(x => x.Id == quantityLinkId).Product.BaseUnit.AllowsDecimals && decimal.Truncate(item.Quantity) != item.Quantity) return "La unidad del material no admite fracciones.";
            if (item.ReservationId is Guid reservationId)
            {
                var reservation = reservations.SingleOrDefault(x => x.Id == reservationId);
                var line = order.SupplyRequests.SelectMany(x => x.Lines).SingleOrDefault(x => x.Reservations.Any(r => r.Id == reservationId));
                if (reservation is null || line is null || reservation.Quantity - reservation.ReleasedQuantity < item.Quantity || order.Stages.Single(x => x.Id == line.SupplyRequest.WorkOrderStageId).Sequence < order.Stages.Single(x => x.Id == cases[item.CaseId].StageId).Sequence) return "La reserva de almacén no corresponde al proceso o excede lo disponible.";
                if (!order.MaterialPlan.Single(x => x.Id == line.MaterialPlanId).Unit.AllowsDecimals && decimal.Truncate(item.Quantity) != item.Quantity) return "La unidad del material no admite fracciones.";
                line.ReworkCaseId = item.CaseId;
            }
        }
        db.ProductionReworkRetentions.RemoveRange(await db.ProductionReworkRetentions.Where(x => x.ReworkCase.WorkOrderId == order.Id).ToListAsync(token));
        foreach (var item in selections) db.ProductionReworkRetentions.Add(new() { ReworkCaseId = item.CaseId, IssueLinkId = item.IssueLinkId, WarehouseReservationId = item.ReservationId, Quantity = item.Quantity });
        foreach (var line in order.SupplyRequests.SelectMany(x => x.Lines))
        {
            var keep = selections.Where(x => x.ReservationId != null && line.Reservations.Any(r => r.Id == x.ReservationId)).ToArray();
            if (keep.Select(x => x.CaseId).Distinct().Count() > 1) return "Una línea de almacén sólo puede retenerse para un retrabajo.";
            foreach (var reservation in line.Reservations)
                reservation.ReleasedQuantity = reservation.Quantity - (keep.SingleOrDefault(x => x.ReservationId == reservation.Id)?.Quantity ?? 0);
            var pending = Pending(line);
            line.CancelledQuantity += Math.Max(0, pending - keep.Sum(x => x.Quantity));
            Invalidate(line);
        }
        return null;
    }

    private async Task<string?> RequestAsync(ProductionWorkOrder order, ProductionExecutionCommand command, CancellationToken token)
    {
        if (order.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress or ProductionWorkOrderStatus.PrincipalClosed)) return "La orden no admite solicitudes.";
        var rework = (await GetReworkAsync(order.Id, token)).SingleOrDefault(x => x.Id == command.CaseId && x.NeedsAttention);
        var plan = order.MaterialPlan.SingleOrDefault(x => x.Id == command.PlanId);
        if (rework is null || plan is null || order.Stages.Single(x => x.Id == plan.WorkOrderStageId).Sequence < order.Stages.Single(x => x.Id == rework.StageId).Sequence || command.Quantity <= 0 || decimal.Round(command.Quantity, 4) != command.Quantity || (!plan.Unit.AllowsDecimals && decimal.Truncate(command.Quantity) != command.Quantity)) return "Selecciona retrabajo, material de su proceso o de uno posterior y cantidad válida.";
        var destinationCode = plan.WipTargetCode ?? "WIP por seleccionar";
        var request = order.SupplyRequests.SingleOrDefault(x => x.WorkOrderStageId == plan.WorkOrderStageId && x.DestinationCode == destinationCode);
        if (request is null)
        {
            request = new ProductionSupplyRequest
            {
                WorkOrderId = order.Id,
                WorkOrderStageId = plan.WorkOrderStageId,
                DestinationLocationId = plan.WipLocationId,
                DestinationCode = destinationCode,
                CreatedAt = clock.GetUtcNow()
            };
            db.ProductionSupplyRequests.Add(request);
        }
        db.ProductionSupplyRequestLines.Add(new()
        {
            SupplyRequest = request,
            MaterialPlanId = plan.Id,
            ProductId = plan.MaterialProductId,
            UnitId = plan.UnitId,
            RequiredQuantity = command.Quantity,
            ReworkCaseId = rework.Id
        });
        request.Status = ProductionSupplyRequestStatus.InProgress; request.Version++; order.UsesSupplyRequests = true;
        return null;
    }

    internal async Task<Dictionary<Guid, decimal>> RemainingWipAsync(Guid orderId, CancellationToken token)
    {
        var reversed = await db.ProductionMaterialOperations.Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var links = await db.ProductionMaterialIssueLinks.Include(x => x.OperationLines).ThenInclude(x => x.Operation).Where(x => x.WorkOrderId == orderId).ToListAsync(token);
        return links.ToDictionary(x => x.Id, x => x.Quantity - x.CancelledQuantity - x.OperationLines.Where(l => l.Operation.Type != ProductionMaterialOperationType.Reversal && !reversed.Contains(l.Operation.Id)).Sum(l => l.Quantity));
    }

    private async Task<string> SnapshotAsync(ProductionWorkOrder order, CancellationToken token) => JsonSerializer.Serialize(new
    {
        order.OriginalTargetQuantity,
        order.TargetQuantity,
        order.AuthorizedQuantity,
        order.DueDate,
        order.Status,
        order.PrincipalClosedAt,
        order.ClosedAt,
        order.Version,
        Received = order.Events.Where(x => x.Type == ProductionEventType.WarehouseReceived).Sum(x => x.Quantity),
        Difference = order.Events.Where(x => x.Type == ProductionEventType.WarehouseReceived).Sum(x => x.Quantity) - order.TargetQuantity,
        Rework = await GetReworkAsync(order.Id, token),
        Retentions = await RetentionSnapshotAsync(order.Id, token),
        Materials = order.MaterialPlan.Select(x => new { x.Id, x.PlannedQuantity }),
        Lines = order.SupplyRequests.SelectMany(x => x.Lines).Select(x => new
        {
            x.Id,
            x.RequiredQuantity,
            x.CancelledQuantity,
            x.ReworkCaseId,
            Reservations = x.Reservations.Select(r => new { r.Id, r.Quantity, r.ReleasedQuantity })
        })
    });
    private async Task<IReadOnlyList<ExecutionRetention>> RetentionSnapshotAsync(Guid orderId, CancellationToken token)
    {
        var existing = await db.ProductionReworkRetentions.AsNoTracking().Where(x => x.ReworkCase.WorkOrderId == orderId).ToListAsync(token);
        var deleted = db.ChangeTracker.Entries<ProductionReworkRetention>().Where(x => x.State == EntityState.Deleted).Select(x => x.Entity.Id).ToHashSet();
        var added = db.ChangeTracker.Entries<ProductionReworkRetention>().Where(x => x.State == EntityState.Added).Select(x => x.Entity);
        return existing.Where(x => !deleted.Contains(x.Id)).Concat(added).Select(x => new ExecutionRetention(x.ReworkCaseId, x.IssueLinkId, x.WarehouseReservationId, x.Quantity)).ToArray();
    }
    private void Invalidate(ProductionSupplyRequestLine line)
    {
        foreach (var preparation in line.Preparations.Where(x => x.Status == ProductionSupplyPreparationStatus.Open))
        { preparation.Status = ProductionSupplyPreparationStatus.Discarded; preparation.Version++; preparation.UpdatedAt = clock.GetUtcNow(); }
        line.SupplyRequest.Version++; ProductionSupplyService.UpdateStatus(line.SupplyRequest);
    }
    public static decimal Pending(ProductionSupplyRequestLine line) => Math.Max(0, line.RequiredQuantity - line.CancelledQuantity + line.ReopenedQuantity - line.IssueLinks.Sum(x => x.Quantity - x.CancelledQuantity));
    private static void Release(ProductionSupplyRequestLine line, decimal quantity)
    {
        foreach (var reservation in line.Reservations.OrderByDescending(x => x.CreatedAt))
        { var release = Math.Min(quantity, reservation.Quantity - reservation.ReleasedQuantity); reservation.ReleasedQuantity += release; quantity -= release; }
    }
    internal static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static ProductionCommandResult Invalid(string error) => new(ProductionCommandStatus.ValidationFailed, Errors: [error]);

    public async Task<ProductionCommandResult> SaveReasonAsync(Guid operationId, Guid id, uint version,
        ProductionReasonCategory category, string code, string description, bool active, bool requiresComment,
        string pin, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(pin, token);
        if (user?.Role.Code != "ADMIN") return new(ProductionCommandStatus.InvalidPin);
        code = code.Trim().ToUpperInvariant(); description = description.Trim();
        if (operationId == Guid.Empty || id == Guid.Empty || !Enum.IsDefined(category) || code.Length is < 1 or > 40 || description.Length is < 1 or > 160)
            return Invalid("Indica código, categoría y descripción válidos.");
        var fingerprint = Hash(new { id, version, category, code, description, active, requiresComment, UserId = user.Id });
        var prior = await db.ProductionExecutionAudits.SingleOrDefaultAsync(x => x.OperationId == operationId, token);
        if (prior is not null) return prior.Fingerprint == fingerprint ? new(ProductionCommandStatus.Success) : new(ProductionCommandStatus.IdempotencyConflict);
        var reason = await db.ProductionReasons.SingleOrDefaultAsync(x => x.Id == id, token);
        if (reason is not null && reason.Version != version) return new(ProductionCommandStatus.ConcurrencyConflict);
        if (reason?.Code == "OTRO" && (code != "OTRO" || !active || !requiresComment || category != reason.Category)) return Invalid("Otro debe permanecer activo y exigir comentario en su categoría.");
        if (await db.ProductionReasons.AnyAsync(x => x.Id != id && x.Category == category && x.Code == code, token)) return Invalid("El código ya existe en esta categoría.");
        var before = JsonSerializer.Serialize(reason);
        if (reason is null) { reason = new() { Id = id }; db.ProductionReasons.Add(reason); }
        reason.Code = code; reason.Description = description; reason.Category = category;
        reason.IsActive = active; reason.RequiresComment = requiresComment; reason.Version++;
        db.ProductionExecutionAudits.Add(new()
        {
            OperationId = operationId,
            Fingerprint = fingerprint,
            Action = "reason",
            ResponsibleUserId = user.Id,
            AuthorizedByUserId = user.Id,
            Reason = "Mantenimiento de motivo",
            BeforeJson = before,
            AfterJson = JsonSerializer.Serialize(reason),
            RecordedAt = clock.GetUtcNow()
        });
        try { await db.SaveChangesAsync(token); return new(ProductionCommandStatus.Success); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return new(ProductionCommandStatus.ConcurrencyConflict); }
    }
}
