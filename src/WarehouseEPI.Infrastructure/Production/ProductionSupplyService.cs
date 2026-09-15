using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionSupplyQueueRow(Guid RequestId, Guid LineId, uint RequestVersion,
    Guid WorkOrderId, Guid WorkOrderStageId, uint WorkOrderVersion, string WorkOrderNumber, string FinishedProduct,
    ProductionSupplyPriority Priority, DateOnly? DueDate, ProductionWorkOrderStatus OrderStatus,
    string Process, Guid ProductId, string Material, string Unit, decimal Required, decimal Cancelled,
    decimal Delivered, decimal Reserved, decimal Pending, decimal Shortage, string Destination,
    Guid? DestinationLocationId, Guid? SuggestedSourceLocationId, string? LastParticipant, string? Problem);

public enum ProductionSupplyCommandStatus { Success, InvalidPin, NotFound, ValidationFailed, ConcurrencyConflict, IdempotencyConflict }
public sealed record ProductionSupplyCommandResult(ProductionSupplyCommandStatus Status,
    Guid? RequestId = null, IReadOnlyList<string>? Errors = null)
{ public IReadOnlyList<string> ValidationErrors => Errors ?? []; }
public sealed record ProductionSupplyLineCommand(Guid OperationId, Guid LineId, uint ExpectedVersion,
    decimal Quantity, string Pin, string? Reason = null);
public sealed record ProductionSupplyRequestCommand(Guid OperationId, Guid RequestId, uint ExpectedVersion,
    string Pin, string? Reason = null);
public sealed record ProductionSupplyPriorityCommand(Guid OperationId, Guid RequestId, uint ExpectedVersion,
    ProductionSupplyPriority Priority, string Pin, string Reason);

public sealed class ProductionSupplyService(WarehouseDbContext db, UserPinService pins, TimeProvider timeProvider)
{
    public async Task<int> GetPendingOrderCountAsync(CancellationToken token = default) => await db.ProductionSupplyRequests
        .AsNoTracking().Where(x => x.Lines.Any(line =>
            line.RequiredQuantity - line.CancelledQuantity - line.IssueLinks.Sum(issue => issue.InventoryMovementLine.Quantity) > 0))
        .Select(x => x.WorkOrderId).Distinct().CountAsync(token);

    public async Task<IReadOnlyList<ProductionSupplyQueueRow>> GetQueueAsync(string? search = null,
        string? condition = null, CancellationToken token = default)
    {
        var query = db.ProductionSupplyRequestLines.AsNoTracking()
            .Include(x => x.Product).ThenInclude(x => x.BaseUnit)
            .Include(x => x.SupplyRequest).ThenInclude(x => x.WorkOrder).ThenInclude(x => x.Product)
            .Include(x => x.SupplyRequest).ThenInclude(x => x.WorkOrderStage)
            .Include(x => x.SupplyRequest).ThenInclude(x => x.Events).ThenInclude(x => x.ResponsibleUser)
            .Include(x => x.Reservations).Include(x => x.IssueLinks).ThenInclude(x => x.InventoryMovementLine)
            .Where(x => x.RequiredQuantity - x.CancelledQuantity - x.IssueLinks.Sum(i => i.InventoryMovementLine.Quantity) > 0);
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term)) query = query.Where(x => x.SupplyRequest.WorkOrder.Number.ToUpper().Contains(term.ToUpper()) ||
            x.SupplyRequest.WorkOrder.Product.Sku.ToUpper().Contains(term.ToUpper()) || x.Product.Sku.ToUpper().Contains(term.ToUpper()));
        var rows = await query.ToListAsync(token);
        var result = rows.Select(ToRow);
        result = condition?.ToLowerInvariant() switch
        {
            "shortage" => result.Where(x => x.Shortage > 0),
            "partial" => result.Where(x => x.Delivered > 0),
            "blocked" => result.Where(x => x.OrderStatus == ProductionWorkOrderStatus.Paused),
            _ => result
        };
        return result.OrderByDescending(x => x.Priority).ThenBy(x => x.DueDate is null).ThenBy(x => x.DueDate)
            .ThenBy(x => x.WorkOrderNumber).ThenBy(x => x.Process).ThenBy(x => x.Material).ToArray();
    }

    internal static async Task GenerateForReleaseAsync(WarehouseDbContext db, ProductionWorkOrder order,
        DateTimeOffset now, CancellationToken token)
    {
        if (order.UsesSupplyRequests || await db.ProductionSupplyRequests.AnyAsync(x => x.WorkOrderId == order.Id, token)) return;
        order.UsesSupplyRequests = true;
        foreach (var group in order.MaterialPlan.GroupBy(x => new { x.WorkOrderStageId, x.WipTargetCode, x.WipLocationId }))
        {
            var request = new ProductionSupplyRequest { WorkOrder = order, WorkOrderStageId = group.Key.WorkOrderStageId,
                DestinationCode = group.Key.WipTargetCode ?? "WIP por seleccionar", DestinationLocationId = group.Key.WipLocationId, CreatedAt = now };
            foreach (var plan in group)
            {
                var line = new ProductionSupplyRequestLine { SupplyRequest = request, MaterialPlanId = plan.Id,
                    ProductId = plan.MaterialProductId, Product = plan.MaterialProduct, UnitId = plan.UnitId,
                    RequiredQuantity = plan.PlannedQuantity };
                request.Lines.Add(line);
                await AllocateAsync(db, line, plan.PlannedQuantity, now, token);
            }
            db.ProductionSupplyRequests.Add(request);
        }
    }

    public Task<ProductionSupplyCommandResult> StartPreparationAsync(ProductionSupplyRequestCommand command,
        CancellationToken token = default) => RequestEventAsync(command, ProductionSupplyEventType.PreparationStarted, false, token);

    public Task<ProductionSupplyCommandResult> ReportProblemAsync(ProductionSupplyRequestCommand command,
        CancellationToken token = default) => RequestEventAsync(command, ProductionSupplyEventType.ProblemReported, false, token, true);

    public async Task<ProductionSupplyCommandResult> ReserveAvailableAsync(ProductionSupplyLineCommand command,
        CancellationToken token = default)
    {
        var user = await OperatorAsync(command.Pin, token); if (user is null) return new(ProductionSupplyCommandStatus.InvalidPin);
        if (command.Quantity <= 0) return Invalid("Indica una cantidad positiva para reservar.");
        var fp = Fingerprint(command with { Pin = string.Empty }); var prior = await ExistingAsync(command.OperationId, fp, token); if (prior is not null) return prior;
        var line = await LoadLineAsync(command.LineId, token); if (line is null) return new(ProductionSupplyCommandStatus.NotFound);
        if (line.SupplyRequest.Version != command.ExpectedVersion) return new(ProductionSupplyCommandStatus.ConcurrencyConflict, line.SupplyRequestId);
        var row = ToRow(line); var requested = Math.Min(command.Quantity, row.Shortage);
        if (requested <= 0) return Invalid("La línea ya tiene reserva suficiente para su pendiente.");
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        var allocated = await AllocateAsync(db, line, requested, timeProvider.GetUtcNow(), token);
        if (allocated <= 0) return await AbortAsync(tx, Invalid("No hay existencia libre disponible para reservar."), token);
        AddEvent(line.SupplyRequest, line, command.OperationId, fp, ProductionSupplyEventType.StockReserved, user, allocated, command.Reason);
        line.SupplyRequest.Version++; await db.SaveChangesAsync(token); if (tx is not null) await tx.CommitAsync(token);
        return new(ProductionSupplyCommandStatus.Success, line.SupplyRequestId);
    }

    public async Task<ProductionSupplyCommandResult> CancelAsync(ProductionSupplyLineCommand command,
        CancellationToken token = default)
    {
        var user = await AdminAsync(command.Pin, token); if (user is null) return new(ProductionSupplyCommandStatus.InvalidPin);
        if (command.Quantity <= 0 || string.IsNullOrWhiteSpace(command.Reason)) return Invalid("Indica la cantidad y el motivo de cancelación.");
        var fp = Fingerprint(command with { Pin = string.Empty }); var prior = await ExistingAsync(command.OperationId, fp, token); if (prior is not null) return prior;
        var line = await LoadLineAsync(command.LineId, token); if (line is null) return new(ProductionSupplyCommandStatus.NotFound);
        if (line.SupplyRequest.Version != command.ExpectedVersion) return new(ProductionSupplyCommandStatus.ConcurrencyConflict, line.SupplyRequestId);
        var pending = ToRow(line).Pending; if (command.Quantity > pending) return Invalid("La cancelación supera la cantidad pendiente.");
        line.CancelledQuantity += command.Quantity; ReleaseReservations(line, command.Quantity);
        AddEvent(line.SupplyRequest, line, command.OperationId, fp, ProductionSupplyEventType.QuantityCancelled, user, command.Quantity, command.Reason);
        line.SupplyRequest.Version++; UpdateStatus(line.SupplyRequest); await db.SaveChangesAsync(token);
        return new(ProductionSupplyCommandStatus.Success, line.SupplyRequestId);
    }

    public async Task<ProductionSupplyCommandResult> ChangePriorityAsync(ProductionSupplyPriorityCommand command,
        CancellationToken token = default)
    {
        var user = await AdminAsync(command.Pin, token); if (user is null) return new(ProductionSupplyCommandStatus.InvalidPin);
        if (string.IsNullOrWhiteSpace(command.Reason)) return Invalid("Indica el motivo del cambio de prioridad.");
        var fp = Fingerprint(command with { Pin = string.Empty }); var prior = await ExistingAsync(command.OperationId, fp, token); if (prior is not null) return prior;
        var request = await db.ProductionSupplyRequests.Include(x => x.WorkOrder).SingleOrDefaultAsync(x => x.Id == command.RequestId, token);
        if (request is null) return new(ProductionSupplyCommandStatus.NotFound); if (request.Version != command.ExpectedVersion) return new(ProductionSupplyCommandStatus.ConcurrencyConflict, request.Id);
        request.WorkOrder.SupplyPriority = command.Priority; AddEvent(request, null, command.OperationId, fp, ProductionSupplyEventType.PriorityChanged, user, reason: command.Reason);
        request.Version++; await db.SaveChangesAsync(token); return new(ProductionSupplyCommandStatus.Success, request.Id);
    }

    internal async Task<(ProductionSupplyRequestLine? Line, string? Error)> ValidateDeliveryAsync(Guid lineId,
        Guid orderId, Guid stageId, Guid productId, Guid? destinationId, decimal quantity, uint expectedVersion, CancellationToken token)
    {
        var line = await LoadLineAsync(lineId, token);
        if (line is null || line.SupplyRequest.WorkOrderId != orderId || line.SupplyRequest.WorkOrderStageId != stageId || line.ProductId != productId) return (null, "La solicitud de surtimiento no corresponde con la orden, proceso y material.");
        if (line.SupplyRequest.Version != expectedVersion) return (null, "La solicitud cambió mientras capturabas. Recarga la cola.");
        if (line.SupplyRequest.WorkOrder.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress)) return (null, "La orden está pausada o ya no admite entregas.");
        if (line.SupplyRequest.DestinationLocationId is Guid expectedDestination && destinationId != expectedDestination) return (null, "La ubicación WIP no coincide con el destino fotografiado de la solicitud.");
        if (quantity > ToRow(line).Pending) return (null, "La cantidad supera el pendiente de la solicitud.");
        return (line, null);
    }

    internal async Task<ProductionSupplyRequestLine?> FindOpenDeliveryLineAsync(Guid orderId, Guid stageId,
        Guid productId, CancellationToken token)
    {
        var candidates = await db.ProductionSupplyRequestLines.AsNoTracking()
            .Where(x => x.SupplyRequest.WorkOrderId == orderId && x.SupplyRequest.WorkOrderStageId == stageId &&
                x.ProductId == productId && x.RequiredQuantity - x.CancelledQuantity -
                x.IssueLinks.Sum(i => i.InventoryMovementLine.Quantity) > 0)
            .Select(x => x.Id).Take(2).ToArrayAsync(token);
        return candidates.Length == 1 ? await LoadLineAsync(candidates[0], token) : null;
    }

    internal void CompleteDelivery(ProductionSupplyRequestLine line, Guid operationId, string fingerprint,
        User user, decimal quantity, Guid movementId, Guid sourceLocationId)
    {
        var remaining = quantity;
        foreach (var reservation in line.Reservations.Where(x => x.LocationId == sourceLocationId && x.Quantity > x.ReleasedQuantity).OrderBy(x => x.CreatedAt))
        { var take = Math.Min(remaining, reservation.Quantity - reservation.ReleasedQuantity); reservation.ReleasedQuantity += take; remaining -= take; if (remaining == 0) break; }
        foreach (var reservation in line.Reservations.Where(x => x.LocationId != sourceLocationId && x.Quantity > x.ReleasedQuantity).OrderBy(x => x.CreatedAt))
        { var take = Math.Min(remaining, reservation.Quantity - reservation.ReleasedQuantity); reservation.ReleasedQuantity += take; remaining -= take; if (remaining == 0) break; }
        AddEvent(line.SupplyRequest, line, operationId, fingerprint, ProductionSupplyEventType.Delivered, user, quantity, movementId: movementId);
        line.SupplyRequest.Version++; UpdateStatus(line.SupplyRequest);
    }

    private async Task<ProductionSupplyCommandResult> RequestEventAsync(ProductionSupplyRequestCommand command,
        ProductionSupplyEventType type, bool admin, CancellationToken token, bool requireReason = false)
    {
        var user = admin ? await AdminAsync(command.Pin, token) : await OperatorAsync(command.Pin, token); if (user is null) return new(ProductionSupplyCommandStatus.InvalidPin);
        if (requireReason && string.IsNullOrWhiteSpace(command.Reason)) return Invalid("Describe el problema para conservar el pendiente.");
        var fp = Fingerprint(command with { Pin = string.Empty }); var prior = await ExistingAsync(command.OperationId, fp, token); if (prior is not null) return prior;
        var request = await db.ProductionSupplyRequests.Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == command.RequestId, token);
        if (request is null) return new(ProductionSupplyCommandStatus.NotFound); if (request.Version != command.ExpectedVersion) return new(ProductionSupplyCommandStatus.ConcurrencyConflict, request.Id);
        var actualType = type == ProductionSupplyEventType.PreparationStarted && request.Events.Any(x => x.Type is ProductionSupplyEventType.PreparationStarted or ProductionSupplyEventType.PreparationContinued) ? ProductionSupplyEventType.PreparationContinued : type;
        AddEvent(request, null, command.OperationId, fp, actualType, user, reason: command.Reason); request.Status = ProductionSupplyRequestStatus.InProgress; request.Version++;
        await db.SaveChangesAsync(token); return new(ProductionSupplyCommandStatus.Success, request.Id);
    }

    private static async Task<decimal> AllocateAsync(WarehouseDbContext db, ProductionSupplyRequestLine line,
        decimal requested, DateTimeOffset now, CancellationToken token)
    {
        var existing = await db.ProductionWarehouseReservations.AsNoTracking().Where(x => x.Quantity > x.ReleasedQuantity)
            .GroupBy(x => new { x.SupplyRequestLine.ProductId, x.LocationId, x.LotId })
            .Select(x => new { x.Key.ProductId, x.Key.LocationId, x.Key.LotId, Quantity = x.Sum(y => y.Quantity - y.ReleasedQuantity) }).ToListAsync(token);
        var used = existing.ToDictionary(x => (x.ProductId, x.LocationId, x.LotId), x => x.Quantity);
        var balances = await db.InventoryBalances.Include(x => x.Location).Include(x => x.Lot)
            .Where(x => x.ProductId == line.ProductId && x.LotId != null && x.Location.IsActive && x.Location.IsPhysicallyPresent && !x.Location.IsBlocked && x.Location.OperationalRole != LocationOperationalRole.Wip)
            .OrderByDescending(x => x.LocationId == x.Product.DefaultEntryLocationId).ThenBy(x => x.Lot!.LotDate == null).ThenBy(x => x.Lot!.LotDate).ThenBy(x => x.Lot!.CreatedAt).ToListAsync(token);
        var remaining = requested;
        foreach (var balance in balances)
        {
            var free = Math.Max(0, balance.Quantity - used.GetValueOrDefault((line.ProductId, balance.LocationId, balance.LotId!.Value)));
            var take = Math.Min(remaining, free); if (take <= 0) continue;
            line.Reservations.Add(new ProductionWarehouseReservation { SupplyRequestLine = line, LocationId = balance.LocationId, LotId = balance.LotId.Value, Quantity = take, CreatedAt = now });
            remaining -= take; if (remaining == 0) break;
        }
        return requested - remaining;
    }

    private Task<ProductionSupplyRequestLine?> LoadLineAsync(Guid id, CancellationToken token) => db.ProductionSupplyRequestLines
        .Include(x => x.Product).ThenInclude(x => x.BaseUnit).Include(x => x.Reservations)
        .Include(x => x.IssueLinks).ThenInclude(x => x.InventoryMovementLine)
        .Include(x => x.SupplyRequest).ThenInclude(x => x.WorkOrder).ThenInclude(x => x.Product)
        .Include(x => x.SupplyRequest).ThenInclude(x => x.WorkOrderStage).Include(x => x.SupplyRequest).ThenInclude(x => x.Events).ThenInclude(x => x.ResponsibleUser)
        .SingleOrDefaultAsync(x => x.Id == id, token);

    private static ProductionSupplyQueueRow ToRow(ProductionSupplyRequestLine line)
    {
        var delivered = line.IssueLinks.Sum(x => x.InventoryMovementLine.Quantity); var pending = Math.Max(0, line.RequiredQuantity - line.CancelledQuantity - delivered);
        var reserved = line.Reservations.Sum(x => x.Quantity - x.ReleasedQuantity); var events = line.SupplyRequest.Events.OrderByDescending(x => x.RecordedAt).ToArray();
        var participant = events.FirstOrDefault(x => x.Type is ProductionSupplyEventType.PreparationStarted or ProductionSupplyEventType.PreparationContinued)?.ResponsibleUser.FullName;
        var problem = events.FirstOrDefault(x => x.Type == ProductionSupplyEventType.ProblemReported)?.Reason;
        return new(line.SupplyRequestId, line.Id, line.SupplyRequest.Version, line.SupplyRequest.WorkOrderId, line.SupplyRequest.WorkOrderStageId, line.SupplyRequest.WorkOrder.Version,
            line.SupplyRequest.WorkOrder.Number, line.SupplyRequest.WorkOrder.Product.Sku, line.SupplyRequest.WorkOrder.SupplyPriority,
            line.SupplyRequest.WorkOrder.DueDate, line.SupplyRequest.WorkOrder.Status, line.SupplyRequest.WorkOrderStage.Name,
            line.ProductId, $"{line.Product.Sku} · {line.Product.Description}", line.Product.BaseUnit.Code, line.RequiredQuantity,
            line.CancelledQuantity, delivered, reserved, pending, Math.Max(0, pending - reserved), line.SupplyRequest.DestinationCode,
            line.SupplyRequest.DestinationLocationId,
            line.Reservations.Where(x => x.Quantity > x.ReleasedQuantity).OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.LocationId).FirstOrDefault(),
            participant, problem);
    }

    private static void ReleaseReservations(ProductionSupplyRequestLine line, decimal quantity)
    { var remaining = quantity; foreach (var r in line.Reservations.Where(x => x.Quantity > x.ReleasedQuantity).OrderByDescending(x => x.CreatedAt)) { var take = Math.Min(remaining, r.Quantity - r.ReleasedQuantity); r.ReleasedQuantity += take; remaining -= take; if (remaining == 0) break; } }
    private static void UpdateStatus(ProductionSupplyRequest request) { if (request.Lines.All(x => x.RequiredQuantity - x.CancelledQuantity - x.IssueLinks.Sum(i => i.InventoryMovementLine.Quantity) <= 0)) request.Status = request.Lines.All(x => x.CancelledQuantity == x.RequiredQuantity) ? ProductionSupplyRequestStatus.Cancelled : ProductionSupplyRequestStatus.Completed; }
    private void AddEvent(ProductionSupplyRequest request, ProductionSupplyRequestLine? line, Guid operationId, string fingerprint, ProductionSupplyEventType type, User user, decimal quantity = 0, string? reason = null, Guid? movementId = null)
    {
        var supplyEvent = new ProductionSupplyEvent { OperationId = operationId, RequestFingerprint = fingerprint,
            SupplyRequest = request, SupplyRequestLine = line, Type = type, ResponsibleUserId = user.Id,
            Quantity = quantity, Reason = Trim(reason), InventoryMovementId = movementId, RecordedAt = timeProvider.GetUtcNow() };
        request.Events.Add(supplyEvent);
        db.Entry(supplyEvent).State = EntityState.Added;
    }
    private async Task<ProductionSupplyCommandResult?> ExistingAsync(Guid operationId, string fingerprint, CancellationToken token) { var e = await db.ProductionSupplyEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operationId, token); return e is null ? null : e.RequestFingerprint == fingerprint ? new(ProductionSupplyCommandStatus.Success, e.SupplyRequestId) : new(ProductionSupplyCommandStatus.IdempotencyConflict, e.SupplyRequestId); }
    private async Task<User?> OperatorAsync(string pin, CancellationToken token) { var u = await pins.AuthenticateAsync(pin, token); return u?.Role.Code is "ADMIN" or "OPERATOR" ? u : null; }
    private async Task<User?> AdminAsync(string pin, CancellationToken token) { var u = await pins.AuthenticateAsync(pin, token); return u?.Role.Code == "ADMIN" ? u : null; }
    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static string? Trim(string? value) { value = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); return value?.Length > 500 ? value[..500] : value; }
    private static ProductionSupplyCommandResult Invalid(string error) => new(ProductionSupplyCommandStatus.ValidationFailed, Errors: [error]);
    private static async Task<ProductionSupplyCommandResult> AbortAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx, ProductionSupplyCommandResult result, CancellationToken token) { if (tx is not null) await tx.RollbackAsync(token); return result; }
}
