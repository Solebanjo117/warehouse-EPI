using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionSupplySourceRow(Guid LocationId, string LocationCode, ProductionSupplySourceKind Kind,
    decimal Physical, decimal ReservedForThisRequest, decimal ReservedForOtherOrders, decimal Available);
public sealed record ProductionSupplyDestinationRow(Guid LocationId, string LocationCode);
public sealed record ProductionSupplyAssignmentRow(Guid IssueLinkId, string LocationCode, decimal Assigned, decimal Available);
public sealed record ProductionSupplySaveResult(Guid OperationId, Guid LineId, Guid? PreparationId, bool IsCurrent, DateTimeOffset RecordedAt);
public sealed record ProductionSupplyConfirmationResult(Guid OperationId, Guid ConfirmationId, Guid SupplyRequestLineId,
    decimal ConfirmedQuantity, decimal PendingQuantity, string Destination, string Responsible, DateTimeOffset RecordedAt,
    int MovementCount, int WipAssignmentCount);
public sealed record ProductionSupplyPreparationView(ProductionSupplyQueueRow Line, Guid DestinationLocationId,
    IReadOnlyList<ProductionSupplyDestinationRow> Destinations, IReadOnlyList<ProductionSupplySourceRow> Sources, Guid? PreparationId, uint PreparationVersion,
    IReadOnlyList<ProductionSupplySourceSelection> Selected, string? PreparedBy, DateTimeOffset? UpdatedAt,
    IReadOnlyList<ProductionSupplyAssignmentRow> CancellableAssignments, bool RequiresReview, bool AllowsDecimals = true);
public sealed record ProductionSupplySourceSelection(ProductionSupplySourceKind Kind, Guid LocationId, decimal Quantity);
public sealed record ProductionSupplySaveCommand(Guid OperationId, Guid LineId, uint ExpectedRequestVersion,
    Guid DestinationLocationId, Guid? PreparationId, uint ExpectedPreparationVersion,
    IReadOnlyList<ProductionSupplySourceSelection> Sources, string Pin);
public sealed record ProductionSupplyConfirmCommand(Guid OperationId, Guid PreparationId, uint ExpectedPreparationVersion,
    uint ExpectedRequestVersion, string Pin, IReadOnlyList<ProductionSupplySourceSelection>? ActualSources = null);
public sealed record ProductionSupplyDiscardCommand(Guid OperationId, Guid PreparationId, uint ExpectedPreparationVersion,
    string Pin, string Reason);
public sealed record ProductionSupplyDestinationCommand(Guid OperationId, Guid LineId, uint ExpectedRequestVersion,
    Guid DestinationLocationId, string Pin, string Reason);
public sealed record ProductionSupplyAssignmentCancelCommand(Guid OperationId, Guid LineId, Guid IssueLinkId,
    uint ExpectedRequestVersion, decimal Quantity, string Pin, string Reason);

public sealed class ProductionSupplyPreparationService(WarehouseDbContext db, UserPinService pins,
    InventoryMovementService movements, TimeProvider timeProvider)
{
    public async Task<Guid?> ResolveLegacyLineAsync(Guid? lineId, Guid? workOrderId, Guid? workOrderStageId,
        Guid? productId, CancellationToken token = default)
    {
        var query = db.ProductionSupplyRequestLines.AsNoTracking()
            .Include(x => x.IssueLinks)
            .Include(x => x.SupplyRequest)
            .Where(x => x.SupplyRequest.Status != ProductionSupplyRequestStatus.Cancelled);
        if (lineId is Guid requestedLineId) query = query.Where(x => x.Id == requestedLineId);
        if (workOrderId is Guid requestedOrderId) query = query.Where(x => x.SupplyRequest.WorkOrderId == requestedOrderId);
        if (workOrderStageId is Guid requestedStageId) query = query.Where(x => x.SupplyRequest.WorkOrderStageId == requestedStageId);
        if (productId is Guid requestedProductId) query = query.Where(x => x.ProductId == requestedProductId);
        var candidates = (await query.ToListAsync(token)).Where(x => Pending(x) > 0).Select(x => x.Id).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public async Task<ProductionSupplyPreparationView?> GetAsync(Guid lineId, CancellationToken token = default)
    {
        var line = await LoadLineAsync(lineId, token);
        if (line is null) return null;
        var row = Row(line);
        var destinations = await CompatibleWipLocationsAsync(line, token);
        var destinationId = line.DestinationLocationId ?? line.SupplyRequest.DestinationLocationId ?? destinations.FirstOrDefault()?.Id;
        if (destinationId is null) return null;
        var sources = await GetSourcesAsync(line, destinationId.Value, token);
        var preparation = line.Preparations.Where(x => x.Status == ProductionSupplyPreparationStatus.Open)
            .OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        var sourceRows = sources.ToList();
        if (preparation is not null)
            foreach (var selected in preparation.Sources.Where(x => sourceRows.All(row => row.Kind != x.Kind || row.LocationId != x.LocationId)))
                sourceRows.Add(new(selected.LocationId, selected.Location.Code, selected.Kind, 0, 0, 0, 0));
        var requiresReview = preparation is not null && (preparation.DestinationLocationId != destinationId.Value ||
            preparation.Sources.Any(selected => !sourceRows.Any(row => row.Kind == selected.Kind && row.LocationId == selected.LocationId && row.Available >= selected.Quantity)));
        var reversed = await db.ProductionMaterialOperations.AsNoTracking().Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var assignments = line.IssueLinks.Where(x => x.Source == ProductionMaterialSupplySource.WipAssignment)
            .Select(x => new ProductionSupplyAssignmentRow(x.Id, x.WipLocation.Code, x.Quantity - x.CancelledQuantity,
                x.Quantity - x.CancelledQuantity - x.OperationLines.Where(y => y.Operation.Type != ProductionMaterialOperationType.Reversal && !reversed.Contains(y.Operation.Id)).Sum(y => y.Quantity)))
            .Where(x => x.Available > 0).ToArray();
        return new(row, destinationId.Value, destinations.Select(x => new ProductionSupplyDestinationRow(x.Id, x.Code)).ToArray(), sourceRows, preparation?.Id, preparation?.Version ?? 0,
            preparation?.Sources.Select(x => new ProductionSupplySourceSelection(x.Kind, x.LocationId, x.Quantity)).ToArray() ?? [],
            preparation?.ResponsibleUser.FullName, preparation?.UpdatedAt, assignments, requiresReview, line.Product.BaseUnit.AllowsDecimals);
    }

    public async Task<ProductionSupplyCommandResult> SaveAsync(ProductionSupplySaveCommand command,
        CancellationToken token = default)
    {
        var user = await OperatorAsync(command.Pin, token); if (user is null) return new(ProductionSupplyCommandStatus.InvalidPin);
        var selections = Normalize(command.Sources); var fp = Fingerprint(command with { Pin = string.Empty, Sources = selections });
        var prior = await ExistingAsync(command.OperationId, fp, token); if (prior is not null) return prior;
        if (selections.Count == 0) return Invalid("Selecciona al menos un origen con cantidad positiva.");
        var line = await LoadLineAsync(command.LineId, token); if (line is null) return new(ProductionSupplyCommandStatus.NotFound);
        if (line.SupplyRequest.Version != command.ExpectedRequestVersion) return Conflict(line.SupplyRequestId);
        if (line.ReworkCaseId is Guid activeCase && !await ProductionExecutionService.ReworkIsOpenAsync(db, activeCase, token)) return Invalid("El retrabajo ya no tiene cantidad pendiente.");
        if ((line.SupplyRequest.WorkOrder.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress or ProductionWorkOrderStatus.PrincipalClosed) || (line.SupplyRequest.WorkOrder.Status == ProductionWorkOrderStatus.PrincipalClosed && line.ReworkCaseId == null))) return Invalid("La orden está pausada o ya no admite entregas.");
        if (!(await CompatibleWipLocationsAsync(line, token)).Any(x => x.Id == command.DestinationLocationId)) return Invalid("El destino ya no es compatible o no está operativo.");
        if (command.Sources.Any(x=>x.Quantity<0 || decimal.Round(x.Quantity,4)!=x.Quantity || (!line.Product.BaseUnit.AllowsDecimals && decimal.Truncate(x.Quantity)!=x.Quantity))) return Invalid("Las cantidades no son válidas para la unidad del material.");
        var pending = Pending(line); if (selections.Sum(x => x.Quantity) > pending) return Invalid("La preparación supera la cantidad pendiente.");
        var available = await GetSourcesAsync(line, command.DestinationLocationId, token);
        if (selections.Any(x => !available.Any(a => a.Kind == x.Kind && a.LocationId == x.LocationId && a.Available >= x.Quantity)))
            return Invalid("La disponibilidad cambió. Revisa los orígenes antes de guardar.");
        var preparation = command.PreparationId is Guid preparationId
            ? line.Preparations.SingleOrDefault(x => x.Id == preparationId && x.Status == ProductionSupplyPreparationStatus.Open)
            : null;
        if (command.PreparationId.HasValue && (preparation is null || preparation.Version != command.ExpectedPreparationVersion)) return Conflict(line.SupplyRequestId);
        var now = timeProvider.GetUtcNow();
        if (preparation is null)
        {
            preparation = new ProductionSupplyPreparation { OperationId = command.OperationId, RequestFingerprint = fp,
                SupplyRequestLine = line, DestinationLocationId = command.DestinationLocationId,
                ResponsibleUserId = user.Id, CreatedAt = now, UpdatedAt = now };
            db.ProductionSupplyPreparations.Add(preparation);
        }
        else
        {
            foreach (var existingSource in preparation.Sources.ToArray())
            {
                var selected = selections.SingleOrDefault(x => x.Kind == existingSource.Kind && x.LocationId == existingSource.LocationId);
                if (selected is null) { preparation.Sources.Remove(existingSource); db.ProductionSupplyPreparationSources.Remove(existingSource); }
                else existingSource.Quantity = selected.Quantity;
            }
            preparation.OperationId = command.OperationId; preparation.RequestFingerprint = fp;
            preparation.DestinationLocationId = command.DestinationLocationId; preparation.ResponsibleUserId = user.Id;
            preparation.UpdatedAt = now; preparation.Version++;
        }
        foreach (var source in selections.Where(x => preparation.Sources.All(y => y.Kind != x.Kind || y.LocationId != x.LocationId)))
            preparation.Sources.Add(new ProductionSupplyPreparationSource { Kind = source.Kind, LocationId = source.LocationId, Quantity = source.Quantity });
        AddEvent(line, command.OperationId, fp, ProductionSupplyEventType.PreparationSaved, user, selections.Sum(x => x.Quantity));
        line.SupplyRequest.Status = ProductionSupplyRequestStatus.InProgress; line.SupplyRequest.Version++;
        try { await db.SaveChangesAsync(token); return new(ProductionSupplyCommandStatus.Success, line.SupplyRequestId); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return await ExistingAsync(command.OperationId, fp, token) ?? Conflict(line.SupplyRequestId); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); var concurrent = await ExistingAsync(command.OperationId, fp, token); if (concurrent is not null) return concurrent; throw; }
    }

    public async Task<ProductionSupplyCommandResult> ConfirmAsync(ProductionSupplyConfirmCommand command,
        CancellationToken token = default)
    {
        var user = await OperatorAsync(command.Pin, token); if (user is null) return new(ProductionSupplyCommandStatus.InvalidPin);
        var requestedSources = command.ActualSources is null ? null : Normalize(command.ActualSources);
        var fp = Fingerprint(command with { Pin = string.Empty, ActualSources = requestedSources }); var prior = await ExistingAsync(command.OperationId, fp, token); if (prior is not null) return prior;
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var preparation = await db.ProductionSupplyPreparations.Include(x => x.Sources)
                .Include(x => x.SupplyRequestLine).ThenInclude(x => x.Product).ThenInclude(x => x.BaseUnit)
                .Include(x => x.SupplyRequestLine).ThenInclude(x => x.Reservations)
                .Include(x => x.SupplyRequestLine).ThenInclude(x => x.IssueLinks)
                .Include(x => x.SupplyRequestLine).ThenInclude(x => x.SupplyRequest).ThenInclude(x => x.WorkOrder)
                .Include(x => x.SupplyRequestLine).ThenInclude(x => x.SupplyRequest).ThenInclude(x => x.WorkOrderStage)
                .Include(x => x.SupplyRequestLine).ThenInclude(x => x.SupplyRequest).ThenInclude(x => x.Lines).ThenInclude(x => x.IssueLinks)
                .SingleOrDefaultAsync(x => x.Id == command.PreparationId, token);
            if (preparation is null || preparation.Status != ProductionSupplyPreparationStatus.Open) return await Abort(tx, new(ProductionSupplyCommandStatus.NotFound), token);
            var line = preparation.SupplyRequestLine;
            if (preparation.Version != command.ExpectedPreparationVersion || line.SupplyRequest.Version != command.ExpectedRequestVersion) return await Abort(tx, Conflict(line.SupplyRequestId), token);
            if (line.ReworkCaseId is Guid activeCase && !await ProductionExecutionService.ReworkIsOpenAsync(db, activeCase, token)) return Invalid("El retrabajo ya no tiene cantidad pendiente.");
        if ((line.SupplyRequest.WorkOrder.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress or ProductionWorkOrderStatus.PrincipalClosed) || (line.SupplyRequest.WorkOrder.Status == ProductionWorkOrderStatus.PrincipalClosed && line.ReworkCaseId == null))) return await Abort(tx, Invalid("La orden está pausada o ya no admite entregas."), token);
            if (preparation.DestinationLocationId != (line.DestinationLocationId ?? line.SupplyRequest.DestinationLocationId)) return await Abort(tx, Invalid("El destino cambió. Guarda nuevamente la preparación después de revisarla."), token);
            if (tx is not null)
            {
                var locationIds = preparation.Sources.Select(x => x.LocationId).Append(preparation.DestinationLocationId).Distinct().Order().ToArray();
                var keys = await db.InventoryBalances.AsNoTracking().Where(x => x.ProductId == line.ProductId && locationIds.Contains(x.LocationId) && x.LotId != null)
                    .Select(x => new InventoryBalanceKey(x.ProductId, x.LocationId, x.LotId)).ToListAsync(token);
                await InventoryMovementStore.LockLocationsAsync(locationIds, tx, token);
                await InventoryMovementStore.LockBalancesAsync(keys, tx, token);
            }
            if (command.ActualSources?.Any(x=>x.Quantity<0 || decimal.Round(x.Quantity,4)!=x.Quantity || (!line.Product.BaseUnit.AllowsDecimals && decimal.Truncate(x.Quantity)!=x.Quantity)) == true) return Invalid("Las cantidades no son válidas para la unidad del material.");
            var confirmedSources = requestedSources ?? Normalize(preparation.Sources.Select(x => new ProductionSupplySourceSelection(x.Kind, x.LocationId, x.Quantity)));
            if (confirmedSources.Count == 0 || confirmedSources.Any(x => !preparation.Sources.Any(saved => saved.Kind == x.Kind && saved.LocationId == x.LocationId && saved.Quantity >= x.Quantity)))
                return await Abort(tx, Invalid("La cantidad entregada debe conservar los orígenes preparados y solo puede reducirse."), token);
            var total = confirmedSources.Sum(x => x.Quantity); if (total <= 0 || total > Pending(line)) return await Abort(tx, Invalid("La cantidad preparada ya no coincide con el pendiente."), token);
            var available = await GetSourcesAsync(line, preparation.DestinationLocationId, token);
            if (confirmedSources.Any(x => !available.Any(a => a.Kind == x.Kind && a.LocationId == x.LocationId && a.Available >= x.Quantity)))
                return await Abort(tx, Invalid("La disponibilidad cambió. Revisa la preparación antes de confirmar."), token);

            var movementIds = new List<Guid>();
            var issueLinks = new List<ProductionMaterialIssueLink>();
            foreach (var source in confirmedSources.Where(x => x.Kind == ProductionSupplySourceKind.Warehouse).OrderBy(x => x.LocationId))
            {
                var movementCommand = new InventoryMovementCommand(Derive(command.OperationId, source.LocationId),
                    InventoryMovementType.Transfer, command.Pin,
                    [new InventoryMovementLineCommand(line.ProductId, source.Quantity, SourceLocationId: source.LocationId,
                        DestinationLocationId: preparation.DestinationLocationId)], line.SupplyRequest.WorkOrder.Number,
                    "Surtimiento guiado a producción", Purpose: InventoryMovementPurpose.ProductionIssue,
                    OperationalAreaId: preparation.DestinationLocationId);
                var movementResult = await movements.ConfirmAuthorizedAsync(movementCommand, user,
                    productionSupplyLineId: line.Id, cancellationToken: token);
                if (movementResult.Status != InventoryMovementStatus.Success || movementResult.MovementId is not Guid movementId)
                    return await Abort(tx, Invalid(movementResult.ValidationErrors.FirstOrDefault() ?? "No fue posible confirmar el traslado."), token);
                movementIds.Add(movementId);
                var movementLine = await db.InventoryMovementLines.Include(x => x.BalanceChanges).SingleAsync(x => x.MovementId == movementId, token);
                var issue = new ProductionMaterialIssueLink { WorkOrderId = line.SupplyRequest.WorkOrderId,
                    WorkOrderStageId = line.SupplyRequest.WorkOrderStageId, SupplyRequestLineId = line.Id,
                    InventoryMovementLineId = movementLine.Id, ProductId = line.ProductId,
                    WipLocationId = preparation.DestinationLocationId, Quantity = source.Quantity,
                    Source = ProductionMaterialSupplySource.Transfer, CreatedAt = timeProvider.GetUtcNow() };
                foreach (var change in movementLine.BalanceChanges.Where(x => x.LocationId == preparation.DestinationLocationId && x.DeltaQuantity > 0 && x.LotId.HasValue))
                    issue.Lots.Add(new ProductionMaterialIssueLot { LotId = change.LotId!.Value, Quantity = change.DeltaQuantity });
                db.ProductionMaterialIssueLinks.Add(issue); issueLinks.Add(issue);
            }
            foreach (var source in confirmedSources.Where(x => x.Kind == ProductionSupplySourceKind.ExistingWip).OrderBy(x => x.LocationId))
            {
                var lots = await AllocateFreeWipLotsAsync(line.ProductId, source.LocationId, source.Quantity, token);
                if (lots.Sum(x => x.Quantity) != source.Quantity) return await Abort(tx, Invalid("El saldo WIP libre cambió. Revisa la preparación."), token);
                var issue = new ProductionMaterialIssueLink { WorkOrderId = line.SupplyRequest.WorkOrderId,
                    WorkOrderStageId = line.SupplyRequest.WorkOrderStageId, SupplyRequestLineId = line.Id,
                    ProductId = line.ProductId, WipLocationId = source.LocationId, Quantity = source.Quantity,
                    Source = ProductionMaterialSupplySource.WipAssignment, CreatedAt = timeProvider.GetUtcNow(),
                    Lots = lots.Select(x => new ProductionMaterialIssueLot { LotId = x.LotId, Quantity = x.Quantity }).ToList() };
                db.ProductionMaterialIssueLinks.Add(issue); issueLinks.Add(issue);
            }
            ReleaseReservations(line, confirmedSources);
            preparation.Status = ProductionSupplyPreparationStatus.Confirmed; preparation.Version++; preparation.UpdatedAt = timeProvider.GetUtcNow();
            AddEvent(line, command.OperationId, fp, confirmedSources.Any(x => x.Kind == ProductionSupplySourceKind.ExistingWip)
                ? ProductionSupplyEventType.WipAssigned : ProductionSupplyEventType.Delivered, user, total,
                movementId: movementIds.Count == 1 ? movementIds[0] : null);
            var confirmation = new ProductionSupplyConfirmation { OperationId = command.OperationId, RequestFingerprint = fp,
                SupplyRequestLine = line, Preparation = preparation, DestinationLocationId = preparation.DestinationLocationId,
                Quantity = total, ResponsibleUserId = user.Id, RecordedAt = timeProvider.GetUtcNow() };
            foreach (var movementId in movementIds) confirmation.Movements.Add(new ProductionSupplyConfirmationMovement { InventoryMovementId = movementId });
            foreach (var issue in issueLinks) confirmation.Issues.Add(new ProductionSupplyConfirmationIssue { IssueLink = issue });
            db.ProductionSupplyConfirmations.Add(confirmation);
            line.SupplyRequest.Version++; line.SupplyRequest.WorkOrder.Version++;
            ProductionSupplyService.UpdateStatus(line.SupplyRequest);
            await db.SaveChangesAsync(token); if (tx is not null) await tx.CommitAsync(token);
            return new(ProductionSupplyCommandStatus.Success, line.SupplyRequestId);
        }
        catch (DbUpdateConcurrencyException) { return await Abort(tx, new(ProductionSupplyCommandStatus.ConcurrencyConflict), token); }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(token); db.ChangeTracker.Clear();
            return await ExistingAsync(command.OperationId, fp, token) ?? new(ProductionSupplyCommandStatus.IdempotencyConflict);
        }
    }

    public async Task<ProductionSupplyCommandResult> DiscardAsync(ProductionSupplyDiscardCommand command, CancellationToken token = default)
    {
        var user = await OperatorAsync(command.Pin, token); if (user is null) return new(ProductionSupplyCommandStatus.InvalidPin);
        if (string.IsNullOrWhiteSpace(command.Reason)) return Invalid("Indica el motivo para descartar la preparación.");
        var fp = Fingerprint(command with { Pin = string.Empty }); var prior = await ExistingAsync(command.OperationId, fp, token); if (prior is not null) return prior;
        var preparation = await db.ProductionSupplyPreparations.Include(x => x.SupplyRequestLine).ThenInclude(x => x.SupplyRequest)
            .SingleOrDefaultAsync(x => x.Id == command.PreparationId, token);
        if (preparation is null || preparation.Status != ProductionSupplyPreparationStatus.Open) return new(ProductionSupplyCommandStatus.NotFound);
        if (preparation.Version != command.ExpectedPreparationVersion) return Conflict(preparation.SupplyRequestLine.SupplyRequestId);
        preparation.Status = ProductionSupplyPreparationStatus.Discarded; preparation.Version++; preparation.UpdatedAt = timeProvider.GetUtcNow();
        AddEvent(preparation.SupplyRequestLine, command.OperationId, fp, ProductionSupplyEventType.PreparationDiscarded, user, reason: command.Reason);
        preparation.SupplyRequestLine.SupplyRequest.Version++; await db.SaveChangesAsync(token);
        return new(ProductionSupplyCommandStatus.Success, preparation.SupplyRequestLine.SupplyRequestId);
    }

    public async Task<ProductionSupplyCommandResult> ChangeDestinationAsync(ProductionSupplyDestinationCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token); if (user?.Role.Code != "ADMIN") return new(ProductionSupplyCommandStatus.InvalidPin);
        if (string.IsNullOrWhiteSpace(command.Reason)) return Invalid("Indica el motivo del cambio de destino.");
        var fp = Fingerprint(command with { Pin = string.Empty }); var prior = await ExistingAsync(command.OperationId, fp, token); if (prior is not null) return prior;
        var line = await LoadLineAsync(command.LineId, token); if (line is null) return new(ProductionSupplyCommandStatus.NotFound);
        if (line.SupplyRequest.Version != command.ExpectedRequestVersion) return Conflict(line.SupplyRequestId);
        var destinations = await CompatibleWipLocationsAsync(line, token);
        var destination = destinations.SingleOrDefault(x => x.Id == command.DestinationLocationId);
        if (destination is null) return Invalid("El destino no es un WIP operativo permitido para el proceso.");
        var before = line.DestinationCode ?? line.SupplyRequest.DestinationCode;
        line.DestinationLocationId = destination.Id; line.DestinationCode = destination.Code;
        foreach (var prep in line.Preparations.Where(x => x.Status == ProductionSupplyPreparationStatus.Open)) { prep.Version++; prep.UpdatedAt = timeProvider.GetUtcNow(); }
        AddEvent(line, command.OperationId, fp, ProductionSupplyEventType.DestinationChanged, user,
            reason: $"{before} → {destination.Code}. {command.Reason.Trim()}");
        line.SupplyRequest.Version++; await db.SaveChangesAsync(token); return new(ProductionSupplyCommandStatus.Success, line.SupplyRequestId);
    }

    public async Task<ProductionSupplyCommandResult> CancelWipAssignmentAsync(ProductionSupplyAssignmentCancelCommand command,
        CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code != "ADMIN") return new(ProductionSupplyCommandStatus.InvalidPin);
        if (command.Quantity <= 0 || decimal.Round(command.Quantity, 4) != command.Quantity) return Invalid("Indica una cantidad válida para anular.");
        if (string.IsNullOrWhiteSpace(command.Reason)) return Invalid("Indica el motivo de la anulación.");
        var fp = Fingerprint(command with { Pin = string.Empty });
        var prior = await ExistingAsync(command.OperationId, fp, token); if (prior is not null) return prior;
        var line = await LoadLineAsync(command.LineId, token); if (line is null) return new(ProductionSupplyCommandStatus.NotFound);
        if (line.SupplyRequest.Version != command.ExpectedRequestVersion) return Conflict(line.SupplyRequestId);
        var issue = line.IssueLinks.SingleOrDefault(x => x.Id == command.IssueLinkId && x.Source == ProductionMaterialSupplySource.WipAssignment);
        if (issue is null) return new(ProductionSupplyCommandStatus.NotFound);
        var reversed = await db.ProductionMaterialOperations.AsNoTracking().Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var used = issue.OperationLines.Where(x => x.Operation.Type != ProductionMaterialOperationType.Reversal && !reversed.Contains(x.Operation.Id)).Sum(x => x.Quantity);
        var available = issue.Quantity - issue.CancelledQuantity - used;
        if (command.Quantity > available) return Invalid("Solo puede anularse la cantidad todavía reservada y sin consumo o devolución dependiente.");
        issue.CancelledQuantity += command.Quantity;
        AddEvent(line, command.OperationId, fp, ProductionSupplyEventType.WipAssignmentCancelled, user, command.Quantity, command.Reason);
        line.SupplyRequest.Version++; line.SupplyRequest.WorkOrder.Version++;
        ProductionSupplyService.UpdateStatus(line.SupplyRequest);
        await db.SaveChangesAsync(token);
        return new(ProductionSupplyCommandStatus.Success, line.SupplyRequestId);
    }

    public async Task<ProductionSupplySaveResult?> GetSaveResultAsync(Guid operationId, Guid lineId, CancellationToken token = default)
    {
        var saved = await db.ProductionSupplyEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operationId && x.SupplyRequestLineId == lineId && x.Type == ProductionSupplyEventType.PreparationSaved, token);
        if (saved is null) return null;
        var current = await db.ProductionSupplyPreparations.AsNoTracking().Where(x => x.SupplyRequestLineId == lineId).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(token);
        return new(operationId, lineId, current?.Id, current?.OperationId == operationId && current.Status == ProductionSupplyPreparationStatus.Open, saved.RecordedAt);
    }

    public async Task<ProductionSupplyCommandResult?> GetResultAsync(Guid operationId, CancellationToken token = default)
    {
        var requestId = await db.ProductionSupplyConfirmations.AsNoTracking()
            .Where(x => x.OperationId == operationId)
            .Select(x => (Guid?)x.SupplyRequestLine.SupplyRequestId)
            .SingleOrDefaultAsync(token);
        return requestId is null ? null : new(ProductionSupplyCommandStatus.Success, requestId);
    }

    public async Task<ProductionSupplyConfirmationResult?> GetConfirmationResultAsync(Guid operationId,
        CancellationToken token = default)
    {
        var result = await db.ProductionSupplyConfirmations.AsNoTracking()
            .Where(x => x.OperationId == operationId)
            .Select(x => new
            {
                x.OperationId,
                ConfirmationId = x.Id,
                x.SupplyRequestLineId,
                ConfirmedQuantity = x.Quantity,
                PendingQuantity = x.SupplyRequestLine.RequiredQuantity - x.SupplyRequestLine.CancelledQuantity
                    + x.SupplyRequestLine.ReopenedQuantity
                    - x.SupplyRequestLine.IssueLinks.Sum(issue => issue.Quantity - issue.CancelledQuantity),
                Destination = x.DestinationLocation.Code,
                Responsible = x.ResponsibleUser.FullName,
                x.RecordedAt,
                MovementCount = x.Movements.Count,
                WipAssignmentCount = x.Issues.Count(issue =>
                    issue.IssueLink.Source == ProductionMaterialSupplySource.WipAssignment)
            })
            .SingleOrDefaultAsync(token);
        return result is null ? null : new(result.OperationId, result.ConfirmationId, result.SupplyRequestLineId,
            result.ConfirmedQuantity, Math.Max(0, result.PendingQuantity), result.Destination, result.Responsible,
            result.RecordedAt, result.MovementCount, result.WipAssignmentCount);
    }

    private async Task<IReadOnlyList<ProductionSupplySourceRow>> GetSourcesAsync(ProductionSupplyRequestLine line,
        Guid destinationId, CancellationToken token)
    {
        var locations = await db.Locations.AsNoTracking().Where(x => x.IsActive && x.IsPhysicallyPresent && !x.IsBlocked).ToListAsync(token);
        var balances = await db.InventoryBalances.AsNoTracking().Where(x => x.ProductId == line.ProductId && x.LotId != null)
            .GroupBy(x => x.LocationId).Select(x => new { LocationId = x.Key, Physical = x.Sum(y => y.Quantity) }).ToListAsync(token);
        var warehouseReservations = await db.ProductionWarehouseReservations.AsNoTracking()
            .Where(x => x.SupplyRequestLine.ProductId == line.ProductId && x.Quantity > x.ReleasedQuantity)
            .GroupBy(x => new { x.SupplyRequestLineId, x.LocationId }).Select(x => new { x.Key.SupplyRequestLineId, x.Key.LocationId, Quantity = x.Sum(y => y.Quantity - y.ReleasedQuantity) }).ToListAsync(token);
        var result = new List<ProductionSupplySourceRow>();
        foreach (var balance in balances)
        {
            var location = locations.SingleOrDefault(x => x.Id == balance.LocationId); if (location is null) continue;
            if (location.OperationalRole != LocationOperationalRole.Wip)
            {
                var own = warehouseReservations.Where(x => x.SupplyRequestLineId == line.Id && x.LocationId == location.Id).Sum(x => x.Quantity);
                var other = warehouseReservations.Where(x => x.SupplyRequestLineId != line.Id && x.LocationId == location.Id).Sum(x => x.Quantity);
                result.Add(new(location.Id, location.Code, ProductionSupplySourceKind.Warehouse, balance.Physical, own, other, Math.Max(0, balance.Physical - other)));
            }
        }
        var compatible = await CompatibleWipLocationsAsync(line, token);
        foreach (var location in compatible.Where(x => x.Id == destinationId))
        {
            var physical = balances.Where(x => x.LocationId == location.Id).Sum(x => x.Physical);
            var reserved = await ActiveWipQuantityAsync(line.ProductId, location.Id, token);
            result.Add(new(location.Id, location.Code, ProductionSupplySourceKind.ExistingWip, physical, 0, reserved, Math.Max(0, physical - reserved)));
        }
        return result.OrderByDescending(x => x.ReservedForThisRequest > 0)
            .ThenByDescending(x => x.LocationId == line.Product.DefaultEntryLocationId)
            .ThenByDescending(x => x.Kind == ProductionSupplySourceKind.Warehouse)
            .ThenBy(x => x.LocationCode).ToArray();
    }

    private async Task<List<Location>> CompatibleWipLocationsAsync(ProductionSupplyRequestLine line, CancellationToken token)
    {
        var locations = await db.Locations.AsNoTracking().Where(x => x.IsActive && x.IsPhysicallyPresent && !x.IsBlocked && x.OperationalRole == LocationOperationalRole.Wip).ToListAsync(token);
        var targets = await db.ProductionProcessWipTargets.AsNoTracking().Where(x => x.ProductionStageId == line.SupplyRequest.WorkOrderStage.SourceStageId).ToListAsync(token);
        var compatible = locations.Where(location => targets.Any(target => target.LocationId == location.Id ||
            target.LocationId == null && target.RowCode == location.RowCode && (target.RackNumber == null || target.RackNumber == location.RackNumber))).ToList();
        if (compatible.Count == 0 && line.SupplyRequest.DestinationLocationId is Guid fallback)
            compatible.AddRange(locations.Where(x => x.Id == fallback));
        return compatible;
    }

    private async Task<decimal> ActiveWipQuantityAsync(Guid productId, Guid locationId, CancellationToken token)
    {
        var reversed = await db.ProductionMaterialOperations.AsNoTracking().Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var links = await db.ProductionMaterialIssueLinks.AsNoTracking().Include(x => x.OperationLines).ThenInclude(x => x.Operation)
            .Where(x => x.ProductId == productId && x.WipLocationId == locationId).ToListAsync(token);
        return links.Sum(x => x.Quantity - x.CancelledQuantity - x.OperationLines.Where(y => y.Operation.Type != ProductionMaterialOperationType.Reversal && !reversed.Contains(y.Operation.Id)).Sum(y => y.Quantity));
    }

    private async Task<IReadOnlyList<InventoryLotSelection>> AllocateFreeWipLotsAsync(Guid productId, Guid locationId, decimal quantity, CancellationToken token)
    {
        var reversed = await db.ProductionMaterialOperations.AsNoTracking().Where(x => x.ReversesOperationId != null).Select(x => x.ReversesOperationId!.Value).ToListAsync(token);
        var links = await db.ProductionMaterialIssueLinks.AsNoTracking().Include(x => x.Lots).ThenInclude(x => x.Lot)
            .Include(x => x.OperationLines).ThenInclude(x => x.Operation).Include(x => x.OperationLines).ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
            .Where(x => x.ProductId == productId && x.WipLocationId == locationId).ToListAsync(token);
        var reserved = links.SelectMany(x => InventoryMovementService.RemainingLots(x, reversed)).GroupBy(x => x.LotId).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        var balances = await db.InventoryBalances.AsNoTracking().Include(x => x.Lot).Where(x => x.ProductId == productId && x.LocationId == locationId && x.LotId != null)
            .OrderBy(x => x.Lot!.LotDate == null).ThenBy(x => x.Lot!.LotDate).ThenBy(x => x.Lot!.CreatedAt).ToListAsync(token);
        var remaining = quantity; var result = new List<InventoryLotSelection>();
        foreach (var balance in balances)
        {
            var take = Math.Min(remaining, Math.Max(0, balance.Quantity - reserved.GetValueOrDefault(balance.LotId!.Value)));
            if (take > 0) result.Add(new(balance.LotId.Value, take)); remaining -= take; if (remaining == 0) break;
        }
        return result;
    }

    private Task<ProductionSupplyRequestLine?> LoadLineAsync(Guid lineId, CancellationToken token) => db.ProductionSupplyRequestLines
        .Include(x => x.Product).ThenInclude(x => x.BaseUnit).Include(x => x.Reservations)
        .Include(x => x.IssueLinks).ThenInclude(x => x.WipLocation)
        .Include(x => x.IssueLinks).ThenInclude(x => x.OperationLines).ThenInclude(x => x.Operation)
        .Include(x => x.Preparations).ThenInclude(x => x.Sources).ThenInclude(x => x.Location).Include(x => x.Preparations).ThenInclude(x => x.ResponsibleUser)
        .Include(x => x.SupplyRequest).ThenInclude(x => x.WorkOrder).ThenInclude(x => x.Product)
        .Include(x => x.SupplyRequest).ThenInclude(x => x.WorkOrderStage).Include(x => x.SupplyRequest).ThenInclude(x => x.Lines).ThenInclude(x => x.IssueLinks)
        .Include(x => x.SupplyRequest).ThenInclude(x => x.Events).ThenInclude(x => x.ResponsibleUser).SingleOrDefaultAsync(x => x.Id == lineId, token);

    private static ProductionSupplyQueueRow Row(ProductionSupplyRequestLine line)
    {
        var delivered = line.IssueLinks.Sum(x => x.Quantity - x.CancelledQuantity); var pending = Pending(line); var reserved = line.Reservations.Sum(x => x.Quantity - x.ReleasedQuantity);
        var events = line.SupplyRequest.Events.OrderByDescending(x => x.RecordedAt).ToArray();
        return new(line.SupplyRequestId, line.Id, line.SupplyRequest.Version, line.SupplyRequest.WorkOrderId, line.SupplyRequest.WorkOrderStageId,
            line.SupplyRequest.WorkOrder.Version, line.SupplyRequest.WorkOrder.Number, line.SupplyRequest.WorkOrder.Product.Sku,
            line.SupplyRequest.WorkOrder.SupplyPriority, line.SupplyRequest.WorkOrder.DueDate, line.SupplyRequest.WorkOrder.Status,
            line.SupplyRequest.WorkOrderStage.Name, line.ProductId, $"{line.Product.Sku} · {line.Product.Description}", line.Product.BaseUnit.Code,
            line.RequiredQuantity, line.CancelledQuantity, delivered, reserved, pending, Math.Max(0, pending - reserved), line.DestinationCode ?? line.SupplyRequest.DestinationCode,
            line.DestinationLocationId ?? line.SupplyRequest.DestinationLocationId, line.Reservations.Where(x => x.Quantity > x.ReleasedQuantity).OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.LocationId).FirstOrDefault(),
            events.FirstOrDefault(x => x.Type is ProductionSupplyEventType.PreparationSaved or ProductionSupplyEventType.PreparationStarted or ProductionSupplyEventType.PreparationContinued)?.ResponsibleUser.FullName,
            events.FirstOrDefault(x => x.Type == ProductionSupplyEventType.ProblemReported)?.Reason);
    }

    private static decimal Pending(ProductionSupplyRequestLine line) => Math.Max(0, line.RequiredQuantity - line.CancelledQuantity + line.ReopenedQuantity - line.IssueLinks.Sum(x => x.Quantity - x.CancelledQuantity));
    private static List<ProductionSupplySourceSelection> Normalize(IEnumerable<ProductionSupplySourceSelection> sources) => sources.Where(x => x.Quantity > 0)
        .GroupBy(x => new { x.Kind, x.LocationId }).Select(x => new ProductionSupplySourceSelection(x.Key.Kind, x.Key.LocationId, x.Sum(y => y.Quantity)))
        .OrderBy(x => x.Kind).ThenBy(x => x.LocationId).ToList();
    private static void ReleaseReservations(ProductionSupplyRequestLine line, IReadOnlyCollection<ProductionSupplySourceSelection> sources)
    {
        var released = 0m;
        foreach (var source in sources.Where(x => x.Kind == ProductionSupplySourceKind.Warehouse).OrderBy(x => x.LocationId))
        {
            var remainingAtSource = source.Quantity;
            foreach (var item in line.Reservations.Where(x => x.LocationId == source.LocationId && x.Quantity > x.ReleasedQuantity).OrderBy(x => x.CreatedAt))
            { var take = Math.Min(remainingAtSource, item.Quantity - item.ReleasedQuantity); item.ReleasedQuantity += take; remainingAtSource -= take; released += take; if (remainingAtSource == 0) break; }
        }
        var remaining = sources.Sum(x => x.Quantity) - released;
        foreach (var item in line.Reservations.Where(x => x.Quantity > x.ReleasedQuantity).OrderBy(x => x.CreatedAt))
        { var take = Math.Min(remaining, item.Quantity - item.ReleasedQuantity); item.ReleasedQuantity += take; remaining -= take; if (remaining == 0) break; }
    }
    private void AddEvent(ProductionSupplyRequestLine line, Guid operationId, string fp, ProductionSupplyEventType type, User user,
        decimal quantity = 0, string? reason = null, Guid? movementId = null) => db.ProductionSupplyEvents.Add(new ProductionSupplyEvent
        { OperationId = operationId, RequestFingerprint = fp, SupplyRequest = line.SupplyRequest, SupplyRequestLine = line,
            Type = type, ResponsibleUserId = user.Id, Quantity = quantity, Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            InventoryMovementId = movementId, RecordedAt = timeProvider.GetUtcNow() });
    private async Task<ProductionSupplyCommandResult?> ExistingAsync(Guid operationId, string fp, CancellationToken token)
    { var e = await db.ProductionSupplyEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operationId, token); return e is null ? null : e.RequestFingerprint == fp ? new(ProductionSupplyCommandStatus.Success, e.SupplyRequestId) : new(ProductionSupplyCommandStatus.IdempotencyConflict, e.SupplyRequestId); }
    private async Task<User?> OperatorAsync(string pin, CancellationToken token) { var user = await pins.AuthenticateAsync(pin, token); return user?.Role.Code is "ADMIN" or "OPERATOR" ? user : null; }
    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static Guid Derive(Guid operationId, Guid sourceId) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId:N}|{sourceId:N}"))[..16]);
    private static ProductionSupplyCommandResult Invalid(string message) => new(ProductionSupplyCommandStatus.ValidationFailed, Errors: [message]);
    private static ProductionSupplyCommandResult Conflict(Guid? requestId = null) => new(ProductionSupplyCommandStatus.ConcurrencyConflict, requestId);
    private static async Task<ProductionSupplyCommandResult> Abort(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx, ProductionSupplyCommandResult result, CancellationToken token)
    { if (tx is not null) await tx.RollbackAsync(token); return result; }
}
