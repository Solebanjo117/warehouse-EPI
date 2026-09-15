using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public enum ProductionMaterialStatus { Success, InvalidPin, ValidationFailed, ConcurrencyConflict, IdempotencyConflict }
public sealed record ProductionMaterialResult(ProductionMaterialStatus Status, Guid? OperationId = null,
    IReadOnlyList<Guid>? MovementIds = null, IReadOnlyList<string>? Errors = null);
public sealed record ProductionMaterialSelection(Guid IssueLinkId, decimal Quantity);
public sealed record ProductionMaterialCommand(Guid OperationId, Guid WorkOrderId, Guid WorkOrderStageId,
    uint ExpectedVersion, ProductionMaterialOperationType Type, IReadOnlyList<ProductionMaterialSelection> Lines,
    string Pin, Guid? DestinationLocationId = null, string? Reference = null, string? Notes = null,
    bool ApproveSharedDestination = false);
public sealed record ProductionMaterialTarget(Guid WorkOrderId, Guid WorkOrderStageId, uint WorkOrderVersion,
    string Label, string Description);
public sealed record ProductionMaterialIssueRow(Guid IssueLinkId, Guid StageId, string ProductSku, string ProductDescription,
    string Unit, string WipCode, decimal Issued, decimal Consumed, decimal WarehouseReturned,
    decimal SupplierReturned, decimal Pending, string Lots = "");
public sealed record ProductionMaterialOperationRow(Guid Id, Guid StageId, ProductionMaterialOperationType Type,
    string Responsible, DateTimeOffset RecordedAt, string? Reference, string? Notes, bool CanReverse);
public sealed record ProductionMaterialReservationRow(Guid WorkOrderId, string WorkOrderNumber, decimal Quantity);
public sealed record ProductionMaterialAvailability(decimal Total, decimal Reserved, decimal Free,
    IReadOnlyList<ProductionMaterialReservationRow> Orders);

public sealed class ProductionMaterialService(WarehouseDbContext db, UserPinService pins,
    InventoryMovementService movements, TimeProvider timeProvider, ProductionSupplyService? supplies = null)
{
    public async Task<ProductionMaterialAvailability> GetAvailabilityAsync(Guid productId, Guid locationId,
        CancellationToken token = default)
    {
        var total = await db.InventoryBalances.AsNoTracking()
            .Where(x => x.ProductId == productId && x.LocationId == locationId)
            .SumAsync(x => x.Quantity, token);
        var reversed = await db.ProductionMaterialOperations.AsNoTracking()
            .Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var links = await db.ProductionMaterialIssueLinks.AsNoTracking()
            .Include(x => x.WorkOrder)
            .Include(x => x.InventoryMovementLine)
            .Include(x => x.OperationLines).ThenInclude(x => x.Operation)
            .Where(x => x.InventoryMovementLine.ProductId == productId &&
                x.InventoryMovementLine.DestinationLocationId == locationId).ToListAsync(token);
        var orders = links.Select(x => new
            {
                x.WorkOrderId,
                x.WorkOrder.Number,
                Quantity = x.InventoryMovementLine.Quantity - x.OperationLines
                    .Where(line => line.Operation.Type != ProductionMaterialOperationType.Reversal &&
                        !reversed.Contains(line.Operation.Id)).Sum(line => line.Quantity)
            })
            .Where(x => x.Quantity > 0)
            .GroupBy(x => new { x.WorkOrderId, x.Number })
            .Select(x => new ProductionMaterialReservationRow(x.Key.WorkOrderId, x.Key.Number, x.Sum(y => y.Quantity)))
            .OrderBy(x => x.WorkOrderNumber).ToArray();
        var reserved = orders.Sum(x => x.Quantity);
        return new(total, reserved, total - reserved, orders);
    }

    public async Task<IReadOnlyList<ProductionMaterialTarget>> SearchTargetsAsync(Guid destinationId, string? search,
        CancellationToken token = default)
    {
        var processIds = await EffectiveProcessIdsAsync(destinationId, token);
        if (processIds.Count == 0) return [];
        var term = search?.Trim();
        var query = db.ProductionWorkOrderStages.AsNoTracking()
            .Where(x => processIds.Contains(x.SourceStageId) &&
                (x.WorkOrder.Status == ProductionWorkOrderStatus.Released || x.WorkOrder.Status == ProductionWorkOrderStatus.InProgress));
        if (!string.IsNullOrWhiteSpace(term))
            query = query.Where(x => x.WorkOrder.Number.ToUpper().Contains(term.ToUpper()) ||
                x.Name.ToUpper().Contains(term.ToUpper()) || x.WorkOrder.Product.Sku.ToUpper().Contains(term.ToUpper()));
        return await query.OrderBy(x => x.WorkOrder.Number).ThenBy(x => x.Sequence).Take(10)
            .Select(x => new ProductionMaterialTarget(x.WorkOrderId, x.Id, x.WorkOrder.Version,
                x.WorkOrder.Number + " · " + x.Name, x.WorkOrder.Product.Sku + " · " + x.WorkOrder.Product.Description))
            .ToListAsync(token);
    }

    public async Task<InventoryMovementResult> IssueAsync(InventoryMovementCommand command, Guid workOrderId,
        Guid stageId, uint expectedVersion, Guid? supplyRequestLineId = null,
        uint? expectedSupplyVersion = null, CancellationToken token = default)
    {
        if (command.Purpose != InventoryMovementPurpose.ProductionIssue || command.Lines.Count != 1 ||
            command.Lines[0].DestinationLocationId is not Guid destinationId)
            return InvalidMovement("El surtimiento vinculado no es válido.");
        var priorMovement = await db.InventoryMovements.AsNoTracking()
            .Include(x => x.Lines).ThenInclude(x => x.MaterialIssueLink)
            .SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (priorMovement is not null)
        {
            var priorLink = priorMovement.Lines.Select(x => x.MaterialIssueLink).SingleOrDefault(x => x is not null);
            if (priorLink?.WorkOrderId != workOrderId || priorLink.WorkOrderStageId != stageId)
                return new(InventoryMovementStatus.IdempotencyConflict);
            return await movements.ConfirmAsync(command, token);
        }
        var stage = await db.ProductionWorkOrderStages.Include(x => x.WorkOrder)
            .SingleOrDefaultAsync(x => x.Id == stageId && x.WorkOrderId == workOrderId, token);
        if (stage is null || stage.WorkOrder.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress))
            return InvalidMovement("La orden o el proceso ya no están disponibles.");
        if (stage.WorkOrder.Version != expectedVersion)
            return new(InventoryMovementStatus.BalanceChanged,
                Errors: ["La orden cambió mientras capturabas. Recarga y selecciona nuevamente el proceso."]);
        ProductionSupplyRequestLine? supplyLine = null;
        var supplyService = supplies ?? new ProductionSupplyService(db, pins, timeProvider);
        if (stage.WorkOrder.UsesSupplyRequests)
        {
            if (supplyRequestLineId is not Guid lineId)
            {
                supplyLine = await supplyService.FindOpenDeliveryLineAsync(workOrderId, stageId,
                    command.Lines[0].ProductId, token);
                if (supplyLine is null) return InvalidMovement("Selecciona una solicitud vigente desde Surtimientos a producción.");
                lineId = supplyLine.Id;
            }
            var supplyVersion = expectedSupplyVersion ?? supplyLine?.SupplyRequest.Version;
            if (supplyVersion is null) return InvalidMovement("Recarga la solicitud antes de confirmar.");
            var validation = await supplyService.ValidateDeliveryAsync(lineId, workOrderId, stageId,
                command.Lines[0].ProductId, destinationId, command.Lines[0].Quantity, supplyVersion.Value, token);
            if (validation.Error is not null) return InvalidMovement(validation.Error);
            supplyLine = validation.Line;
        }
        if (!(await EffectiveProcessIdsAsync(destinationId, token)).Contains(stage.SourceStageId))
            return InvalidMovement("El proceso de la orden no está permitido en el destino WIP seleccionado.");

        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var movementUser = await pins.AuthenticateAsync(command.Pin, token);
            if (movementUser is null || movementUser.Role.Code is not ("ADMIN" or "OPERATOR"))
            {
                if (transaction is not null) await transaction.RollbackAsync(token);
                return new(InventoryMovementStatus.InvalidPin);
            }
            var result = await movements.ConfirmAuthorizedAsync(command, movementUser,
                productionSupplyLineId: supplyLine?.Id, cancellationToken: token);
            if (result.Status != InventoryMovementStatus.Success || result.MovementId is not Guid movementId)
            {
                if (transaction is not null) await transaction.RollbackAsync(token);
                return result;
            }
            var line = await db.InventoryMovementLines.SingleAsync(x => x.MovementId == movementId, token);
            var existing = await db.ProductionMaterialIssueLinks.SingleOrDefaultAsync(x => x.InventoryMovementLineId == line.Id, token);
            if (existing is null)
            {
                db.ProductionMaterialIssueLinks.Add(new ProductionMaterialIssueLink
                {
                    WorkOrderId = workOrderId, WorkOrderStageId = stageId,
                    InventoryMovementLineId = line.Id, SupplyRequestLineId = supplyLine?.Id,
                    CreatedAt = timeProvider.GetUtcNow()
                });
                if (supplyLine is not null && result.ResponsibleUserId is Guid userId)
                {
                    var user = await db.Users.SingleAsync(x => x.Id == userId, token);
                    var supplyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        $"{command.OperationId:N}|{supplyLine.Id:N}|{command.Lines[0].Quantity.ToString(CultureInfo.InvariantCulture)}")));
                    supplyService.CompleteDelivery(supplyLine, command.OperationId, supplyFingerprint, user,
                        command.Lines[0].Quantity, movementId, command.Lines[0].SourceLocationId!.Value);
                }
                stage.WorkOrder.Version++;
                await db.SaveChangesAsync(token);
            }
            if (transaction is not null) await transaction.CommitAsync(token);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(InventoryMovementStatus.BalanceChanged,
                Errors: ["La orden o el inventario cambiaron durante el surtimiento."]);
        }
    }

    public async Task<IReadOnlyList<ProductionMaterialIssueRow>> GetIssuesAsync(Guid workOrderId,
        CancellationToken token = default)
    {
        var reversed = await db.ProductionMaterialOperations.AsNoTracking().Where(x => x.ReversesOperationId != null)
            .Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var links = await db.ProductionMaterialIssueLinks.AsNoTracking()
            .Include(x => x.InventoryMovementLine).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
            .Include(x => x.InventoryMovementLine).ThenInclude(x => x.DestinationLocation)
            .Include(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
            .Include(x => x.OperationLines).ThenInclude(x => x.Operation)
            .Where(x => x.WorkOrderId == workOrderId).ToListAsync(token);
        return links.Select(link =>
        {
            decimal Used(ProductionMaterialOperationType type) => link.OperationLines
                .Where(x => x.Operation.Type == type && !reversed.Contains(x.Operation.Id)).Sum(x => x.Quantity);
            var consumed = Used(ProductionMaterialOperationType.Consumption);
            var warehouse = Used(ProductionMaterialOperationType.WarehouseReturn);
            var supplier = Used(ProductionMaterialOperationType.SupplierReturn);
            var line = link.InventoryMovementLine;
            return new ProductionMaterialIssueRow(link.Id, link.WorkOrderStageId, line.Product.Sku,
                line.Product.Description ?? string.Empty, line.Product.BaseUnit.Code, line.DestinationLocation!.Code,
                line.Quantity, consumed, warehouse, supplier, line.Quantity - consumed - warehouse - supplier,
                string.Join(", ", line.BalanceChanges.Where(x => x.DeltaQuantity > 0 && !string.IsNullOrWhiteSpace(x.LotNumberSnapshot))
                    .Select(x => x.LotNumberSnapshot!).Distinct().OrderBy(x => x)));
        }).OrderBy(x => x.ProductSku).ThenBy(x => x.WipCode).ToArray();
    }

    public async Task<IReadOnlyList<ProductionMaterialOperationRow>> GetOperationsAsync(Guid workOrderId,
        CancellationToken token = default)
    {
        var reversed = await db.ProductionMaterialOperations.AsNoTracking().Where(x => x.ReversesOperationId != null)
            .Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        return await db.ProductionMaterialOperations.AsNoTracking().Where(x => x.WorkOrderId == workOrderId)
            .OrderByDescending(x => x.RecordedAt).Select(x => new ProductionMaterialOperationRow(x.Id,
                x.WorkOrderStageId, x.Type, x.ResponsibleUser.FullName, x.RecordedAt, x.Reference, x.Notes,
                x.Type != ProductionMaterialOperationType.Reversal && !reversed.Contains(x.Id))).ToListAsync(token);
    }

    public async Task<ProductionMaterialResult> ApplyAsync(ProductionMaterialCommand command,
        CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user is null) return new(ProductionMaterialStatus.InvalidPin);
        if (command.OperationId == Guid.Empty || command.Lines.Count == 0 || command.Lines.Any(x => x.Quantity <= 0))
            return Invalid("Selecciona al menos un material con cantidad positiva.");
        if (command.Type == ProductionMaterialOperationType.SupplierReturn && string.IsNullOrWhiteSpace(command.Reference))
            return Invalid("La devolución a proveedor requiere una referencia.");
        if (command.Type == ProductionMaterialOperationType.WarehouseReturn && command.DestinationLocationId is null)
            return Invalid("Selecciona la ubicación de regreso a bodega.");
        var fingerprint = Fingerprint(command, user.Id);
        var prior = await db.ProductionMaterialOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.RequestFingerprint == fingerprint
            ? new(ProductionMaterialStatus.Success, prior.Id)
            : new(ProductionMaterialStatus.IdempotencyConflict);

        var ownsTransaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null;
        await using var ownedTransaction = ownsTransaction ? await db.Database.BeginTransactionAsync(token) : null;
        var transaction = ownedTransaction ?? db.Database.CurrentTransaction;
        try
        {
            var order = await db.ProductionWorkOrders.Include(x => x.Stages)
                .SingleOrDefaultAsync(x => x.Id == command.WorkOrderId, token);
            if (order is null || order.Version != command.ExpectedVersion)
                return await Abort(transaction, new(ProductionMaterialStatus.ConcurrencyConflict), token);
            if (order.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress))
                return await Abort(transaction, Invalid("La orden no está disponible para movimientos de material."), token);
            if (!order.Stages.Any(x => x.Id == command.WorkOrderStageId))
                return await Abort(transaction, Invalid("El proceso no pertenece a la orden."), token);
            var requested = command.Lines.GroupBy(x => x.IssueLinkId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
            var links = await db.ProductionMaterialIssueLinks
                .Include(x => x.InventoryMovementLine).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
                .Include(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
                .Include(x => x.OperationLines).ThenInclude(x => x.Operation)
                .Include(x => x.OperationLines).ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
                .Where(x => requested.Keys.Contains(x.Id)).ToListAsync(token);
            if (links.Count != requested.Count || links.Any(x => x.WorkOrderId != order.Id || x.WorkOrderStageId != command.WorkOrderStageId))
                return await Abort(transaction, Invalid("Una línea de material no pertenece al proceso seleccionado."), token);
            var reversed = await db.ProductionMaterialOperations.AsNoTracking().Where(x => x.ReversesOperationId != null)
                .Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
            foreach (var link in links)
            {
                var used = link.OperationLines.Where(x => x.Operation.Type != ProductionMaterialOperationType.Reversal &&
                    !reversed.Contains(x.Operation.Id)).Sum(x => x.Quantity);
                if (requested[link.Id] > link.InventoryMovementLine.Quantity - used)
                    return await Abort(transaction, Invalid($"La cantidad de {link.InventoryMovementLine.Product.Sku} supera lo pendiente."), token);
            }

            var operation = new ProductionMaterialOperation
            {
                OperationId = command.OperationId, RequestFingerprint = fingerprint, WorkOrderId = order.Id,
                WorkOrderStageId = command.WorkOrderStageId, Type = command.Type, ResponsibleUserId = user.Id,
                Reference = Normalize(command.Reference), Notes = Normalize(command.Notes), RecordedAt = timeProvider.GetUtcNow()
            };
            var movementIds = new List<Guid>();
            foreach (var group in links.OrderBy(x => x.InventoryMovementLine.DestinationLocationId)
                         .ThenBy(x => x.InventoryMovementLine.ProductId).ThenBy(x => x.Id)
                         .GroupBy(x => x.InventoryMovementLine.DestinationLocationId!.Value))
            {
                var movementType = command.Type == ProductionMaterialOperationType.WarehouseReturn
                    ? InventoryMovementType.Transfer : InventoryMovementType.Exit;
                var purpose = command.Type == ProductionMaterialOperationType.Consumption ? InventoryMovementPurpose.WipConsumption :
                    command.Type == ProductionMaterialOperationType.WarehouseReturn ? InventoryMovementPurpose.WipWarehouseReturn : InventoryMovementPurpose.WipSupplierReturn;
                var movementCommand = new InventoryMovementCommand(Derive(command.OperationId, group.Key), movementType, command.Pin,
                    group.Select(link => new InventoryMovementLineCommand(link.InventoryMovementLine.ProductId,
                        requested[link.Id], SourceLocationId: group.Key,
                        DestinationLocationId: command.Type == ProductionMaterialOperationType.WarehouseReturn ? command.DestinationLocationId : null,
                        Lots: AllocateLots(link, requested[link.Id], reversed))).ToArray(),
                    command.Reference, command.Notes,
                    command.ApproveSharedDestination && command.DestinationLocationId is Guid destinationId
                        ? group.Select(link => new SharedAssignmentApproval(link.InventoryMovementLine.ProductId, destinationId)).ToArray()
                        : [],
                    Purpose: purpose, OperationalAreaId: group.Key);
                var movementResult = await movements.ConfirmAuthorizedAsync(movementCommand, user,
                    allowReservedWip: true, cancellationToken: token);
                if (movementResult.Status != InventoryMovementStatus.Success || movementResult.MovementId is not Guid movementId)
                    return await Abort(transaction, Invalid(movementResult.ValidationErrors.FirstOrDefault() ?? "No fue posible mover el material WIP."), token);
                movementIds.Add(movementId);
                var movementLines = await db.InventoryMovementLines.Where(x => x.MovementId == movementId).OrderBy(x => x.LineNumber).ToListAsync(token);
                foreach (var pair in group.Zip(movementLines))
                    operation.Lines.Add(new ProductionMaterialOperationLine
                    {
                        IssueLinkId = pair.First.Id, InventoryMovementLineId = pair.Second.Id,
                        Quantity = requested[pair.First.Id]
                    });
            }
            db.ProductionMaterialOperations.Add(operation);
            order.Version++;
            await db.SaveChangesAsync(token);
            if (ownsTransaction && transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionMaterialStatus.Success, operation.Id, movementIds);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(ProductionMaterialStatus.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            prior = await db.ProductionMaterialOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            return prior?.RequestFingerprint == fingerprint ? new(ProductionMaterialStatus.Success, prior.Id) :
                new(ProductionMaterialStatus.IdempotencyConflict);
        }
    }

    public async Task<ProductionMaterialResult> ReverseAsync(Guid operationId, Guid materialOperationId,
        uint expectedVersion, string pin, string reason, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(pin, token);
        if (user?.Role.Code != "ADMIN") return new(ProductionMaterialStatus.InvalidPin);
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(reason)) return Invalid("Indica el motivo del reverso.");
        var fingerprint = Hash($"{materialOperationId:N}|{user.Id:N}|{reason.Trim()}");
        var prior = await db.ProductionMaterialOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operationId, token);
        if (prior is not null) return prior.RequestFingerprint == fingerprint ? new(ProductionMaterialStatus.Success, prior.Id) : new(ProductionMaterialStatus.IdempotencyConflict);
        var ownsTransaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
        var original = await db.ProductionMaterialOperations.Include(x => x.WorkOrder).Include(x => x.Lines)
            .ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.Movement).ThenInclude(x => x.Lines)
            .ThenInclude(x => x.BalanceChanges).SingleOrDefaultAsync(x => x.Id == materialOperationId, token);
        if (original is null || original.Type == ProductionMaterialOperationType.Reversal ||
            await db.ProductionMaterialOperations.AnyAsync(x => x.ReversesOperationId == original.Id, token))
            return await Abort(transaction, Invalid("La operación no existe o ya fue revertida."), token);
        if (original.WorkOrder.Version != expectedVersion)
            return await Abort(transaction, new(ProductionMaterialStatus.ConcurrencyConflict), token);
        var reversal = new ProductionMaterialOperation { OperationId = operationId, RequestFingerprint = fingerprint,
            WorkOrderId = original.WorkOrderId, WorkOrderStageId = original.WorkOrderStageId,
            Type = ProductionMaterialOperationType.Reversal, ResponsibleUserId = user.Id,
            ReversesOperationId = original.Id, Notes = reason.Trim(), RecordedAt = timeProvider.GetUtcNow() };
        var reversalService = new InventoryReversalService(db, timeProvider);
        foreach (var movement in original.Lines.Select(x => x.InventoryMovementLine.Movement)
                     .DistinctBy(x => x.Id).OrderBy(x => x.Id))
        {
            var reversedMovement = await reversalService.CreateAsync(movement, user.Id, fingerprint, token);
            foreach (var originalLine in original.Lines.Where(x => x.InventoryMovementLine.MovementId == movement.Id))
            {
                var reversedLine = reversedMovement.Lines.Single(x => x.LineNumber == originalLine.InventoryMovementLine.LineNumber);
                reversal.Lines.Add(new ProductionMaterialOperationLine { IssueLinkId = originalLine.IssueLinkId,
                    InventoryMovementLine = reversedLine, Quantity = originalLine.Quantity });
            }
        }
        db.ProductionMaterialOperations.Add(reversal); original.WorkOrder.Version++;
        await db.SaveChangesAsync(token); if (transaction is not null) await transaction.CommitAsync(token);
        return new(ProductionMaterialStatus.Success, reversal.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(ProductionMaterialStatus.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            prior = await db.ProductionMaterialOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == operationId, token);
            return prior?.RequestFingerprint == fingerprint
                ? new(ProductionMaterialStatus.Success, prior.Id)
                : new(ProductionMaterialStatus.IdempotencyConflict);
        }
    }

    private async Task<List<Guid>> EffectiveProcessIdsAsync(Guid locationId, CancellationToken token)
    {
        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == locationId, token);
        if (location is null) return [];
        if (location.Kind == LocationKind.Area)
            return await db.ProductionProcessWipTargets.AsNoTracking().Where(x => x.LocationId == locationId)
                .Select(x => x.ProductionStageId).Distinct().ToListAsync(token);
        return await db.ProductionProcessWipTargets.AsNoTracking().Where(x => x.RowCode == location.RowCode &&
                (x.RackNumber == null || x.RackNumber == location.RackNumber))
            .Select(x => x.ProductionStageId).Distinct().ToListAsync(token);
    }

    private static InventoryMovementResult InvalidMovement(string error) => new(InventoryMovementStatus.ValidationFailed, Errors: [error]);
    private static ProductionMaterialResult Invalid(string error) => new(ProductionMaterialStatus.ValidationFailed, Errors: [error]);
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Fingerprint(ProductionMaterialCommand command, Guid userId) => Hash(Json(command, userId));
    private static string Json(ProductionMaterialCommand command, Guid userId) => string.Join('|',
        userId, command.WorkOrderId, command.WorkOrderStageId, command.ExpectedVersion, command.Type,
        command.DestinationLocationId, Normalize(command.Reference), Normalize(command.Notes), command.ApproveSharedDestination,
        string.Join(';', command.Lines.OrderBy(x => x.IssueLinkId).Select(x => $"{x.IssueLinkId:N}:{x.Quantity.ToString("G29", CultureInfo.InvariantCulture)}")));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Guid Derive(Guid operationId, Guid group) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId:N}|{group:N}"))[..16]);
    private static IReadOnlyList<InventoryLotSelection> AllocateLots(
        ProductionMaterialIssueLink link, decimal quantity, IReadOnlyCollection<Guid> reversed)
    {
        var remaining = quantity;
        var result = new List<InventoryLotSelection>();
        foreach (var lot in InventoryMovementService.RemainingLots(link, reversed))
        {
            var take = Math.Min(remaining, lot.Quantity);
            if (take > 0) result.Add(new(lot.LotId, take));
            remaining -= take;
            if (remaining == 0) break;
        }
        if (remaining != 0) throw new InvalidOperationException("Los lotes reservados ya no cubren la cantidad pendiente.");
        return result;
    }
    private static async Task<ProductionMaterialResult> Abort(IDbContextTransaction? tx, ProductionMaterialResult result, CancellationToken token)
    { if (tx is not null) await tx.RollbackAsync(token); return result; }
}
