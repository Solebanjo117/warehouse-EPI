using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record RecipeLineInput(Guid MaterialProductId, Guid StageId, decimal Quantity);
public sealed record SaveProductionRecipeCommand(Guid ProductId, decimal BaseQuantity,
    IReadOnlyList<RecipeLineInput> Lines, string Reason, string Pin);
public sealed record CreateProductionBatchCommand(Guid OperationId, Guid WorkOrderId,
    decimal AssignedQuantity, uint ExpectedOrderVersion, string Pin);
public sealed record BatchMaterialInput(Guid IssueLinkId, decimal Quantity);
public sealed record RecordBatchResultCommand(Guid OperationId, Guid WorkOrderId, Guid BatchId,
    Guid StageId, Guid ShiftId, bool IsRework, decimal InputQuantity, decimal GoodQuantity,
    decimal ReworkQuantity, decimal ScrapQuantity, IReadOnlyList<BatchMaterialInput> Materials,
    uint ExpectedOrderVersion, string? DifferenceReason, string Pin);
public sealed record ProductionTraceabilityResult(bool Success, Guid? Id = null,
    IReadOnlyList<string>? Errors = null, bool Conflict = false);
public sealed record ProductionRecipeView(Guid Id, Guid ProductId, string Product,
    int Version, decimal BaseQuantity, IReadOnlyList<ProductionRecipeLineView> Lines);
public sealed record ProductionRecipeLineView(Guid MaterialProductId, string Material, Guid StageId,
    string Stage, decimal Quantity, string Unit);
public sealed record ProductionProductStageView(Guid Id, int Sequence, string Code, string Name);
public sealed record ProductionProductConfigurationView(bool ProductIsActive, Guid? RouteId, string? RouteName,
    IReadOnlyList<ProductionProductStageView> Stages, ProductionRecipeView? ActiveRecipe);
public sealed record ProductionBatchView(Guid Id, string Number, decimal Assigned, decimal Processed,
    decimal Good, decimal Rework, decimal Scrap, decimal WarehouseReceived, string CurrentStage,
    Guid FinishedProductLotId);
public sealed record ProductionMaterialPlanView(Guid Id, Guid StageId, string Stage, Guid ProductId,
    string Sku, string Description, string Unit, decimal Planned, decimal Issued, decimal Consumed,
    decimal Returned, decimal Pending, bool HasDifference);
public sealed record ProductionTraceLinkView(string BatchNumber, string FinishedLot, string FinishedSku,
    string MaterialLot, string MaterialSku, decimal Quantity, string Unit, string Stage,
    string Responsible, DateTimeOffset RecordedAt);
public sealed record ProductionDeliveryView(Guid Id, Guid BatchId, string BatchNumber, Guid SourceStageId,
    Guid TargetStageId, decimal Delivered, decimal Pending, DateTimeOffset RecordedAt);
public sealed record ProductionBatchResultView(Guid Id, Guid BatchId, string Stage, string Shift, bool IsRework,
    decimal Input, decimal Good, decimal Rework, decimal Scrap, string Responsible, DateTimeOffset RecordedAt,
    bool CanReverse);

public sealed class ProductionTraceabilityService(WarehouseDbContext db, UserPinService pins,
    ProductionMaterialService materialService, TimeProvider timeProvider)
{
    public async Task<ProductionTraceabilityResult> SaveRecipeAsync(SaveProductionRecipeCommand command,
        CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code != "ADMIN") return Invalid("NIP ADMIN inválido.");
        if (!await db.Products.AnyAsync(x => x.Id == command.ProductId && x.IsActive, token))
            return Invalid("El producto terminado no existe o está inactivo.");
        var lines = command.Lines.Where(x => x.MaterialProductId != Guid.Empty && x.StageId != Guid.Empty && x.Quantity > 0)
            .GroupBy(x => new { x.MaterialProductId, x.StageId })
            .Select(x => new RecipeLineInput(x.Key.MaterialProductId, x.Key.StageId, x.Sum(y => y.Quantity))).ToArray();
        if (command.BaseQuantity <= 0 || decimal.Round(command.BaseQuantity, 4) != command.BaseQuantity || lines.Length == 0)
            return Invalid("Indica una cantidad base y al menos un material válido.");
        if (string.IsNullOrWhiteSpace(command.Reason)) return Invalid("Indica el motivo de la nueva versión.");
        var route = await db.ProductionRoutes.Include(x => x.Stages)
            .SingleOrDefaultAsync(x => x.ProductId == command.ProductId && x.IsActive, token);
        if (route is null) return Invalid("El producto requiere una ruta activa.");
        if (lines.Any(x => !route.Stages.Any(s => s.StageId == x.StageId)))
            return Invalid("Todos los materiales deben incorporarse en una etapa de la ruta activa.");
        var products = await db.Products.Include(x => x.BaseUnit)
            .Where(x => lines.Select(y => y.MaterialProductId).Contains(x.Id) && x.IsActive).ToListAsync(token);
        if (products.Count != lines.Select(x => x.MaterialProductId).Distinct().Count())
            return Invalid("Uno de los materiales no existe o está inactivo.");
        if (lines.Any(x => !products.Single(p => p.Id == x.MaterialProductId).BaseUnit.AllowsDecimals && decimal.Truncate(x.Quantity) != x.Quantity))
            return Invalid("Una cantidad usa decimales en una unidad que no los admite.");
        var prior = await db.ProductionRecipes.Where(x => x.ProductId == command.ProductId)
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(token);
        if (prior is not null) prior.IsActive = false;
        var recipe = new ProductionRecipe { ProductId = command.ProductId, Version = (prior?.Version ?? 0) + 1,
            BaseQuantity = command.BaseQuantity, CreatedByUserId = user.Id, CreatedAt = timeProvider.GetUtcNow(),
            Reason = command.Reason.Trim() };
        foreach (var line in lines) recipe.Lines.Add(new ProductionRecipeLine
            { MaterialProductId = line.MaterialProductId, StageId = line.StageId, Quantity = line.Quantity });
        db.ProductionRecipes.Add(recipe);
        await db.SaveChangesAsync(token);
        return new(true, recipe.Id);
    }

    public async Task<ProductionTraceabilityResult> AdjustPlanAsync(Guid operationId, Guid planId, decimal quantity,
        uint expectedVersion, string reason, string pin, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(pin, token);
        if (user?.Role.Code != "ADMIN") return Invalid("NIP ADMIN inválido.");
        if (operationId == Guid.Empty || quantity <= 0 || decimal.Round(quantity, 4) != quantity || string.IsNullOrWhiteSpace(reason)) return Invalid("Indica cantidad positiva y motivo.");
        var fingerprint = Fingerprint(new { planId, quantity, reason = reason.Trim(), user.Id });
        var prior = await db.ProductionEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operationId, token);
        if (prior is not null) return prior.RequestFingerprint == fingerprint ? new(true, prior.Id) : new(false, Conflict: true);
        var plan = await db.ProductionOrderMaterialPlans.Include(x => x.WorkOrder).Include(x => x.Unit)
            .SingleOrDefaultAsync(x => x.Id == planId, token);
        if (plan is null) return Invalid("El material planeado no existe.");
        if (!plan.Unit.AllowsDecimals && decimal.Truncate(quantity) != quantity)
            return Invalid("La unidad del material no admite decimales.");
        if (plan.WorkOrder.Version != expectedVersion) return new(false, Errors: ["La orden cambió."], Conflict: true);
        plan.PlannedQuantity = quantity;
        plan.AdjustmentReason = reason.Trim();
        plan.AdjustedByUserId = user.Id;
        plan.AdjustedAt = timeProvider.GetUtcNow();
        db.ProductionEvents.Add(new ProductionEvent { OperationId = operationId, RequestFingerprint = fingerprint,
            WorkOrderId = plan.WorkOrderId, WorkOrderStageId = plan.WorkOrderStageId,
            Type = ProductionEventType.MaterialPlanAdjusted, ResponsibleUserId = user.Id, Quantity = quantity,
            Reason = reason.Trim(), RecordedAt = timeProvider.GetUtcNow() });
        plan.WorkOrder.Version++;
        await db.SaveChangesAsync(token);
        return new(true, operationId);
    }

    public async Task<ProductionTraceabilityResult> CreateBatchAsync(CreateProductionBatchCommand command,
        CancellationToken token = default)
    {
        var fp = Fingerprint(command with { Pin = "" });
        var prior = await db.ProductionBatches.AsNoTracking().SingleOrDefaultAsync(x => x.CreateOperationId == command.OperationId, token);
        if (prior is not null) return prior.CreateFingerprint == fp ? new(true, prior.Id) : new(false, Conflict: true);
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code is not ("ADMIN" or "OPERATOR")) return Invalid("NIP inválido.");
        var order = await db.ProductionWorkOrders.Include(x => x.Batches).Include(x => x.Product).Include(x => x.Unit)
            .SingleOrDefaultAsync(x => x.Id == command.WorkOrderId, token);
        if (order is null || !order.UsesBatchTraceability) return Invalid("La orden no tiene seguimiento por lotes.");
        if (order.Status is ProductionWorkOrderStatus.Closed or ProductionWorkOrderStatus.Cancelled)
            return Invalid("La orden ya no admite lotes.");
        if (order.Version != command.ExpectedOrderVersion) return new(false, Errors: ["La orden cambió."], Conflict: true);
        if (command.AssignedQuantity <= 0 || decimal.Round(command.AssignedQuantity, 4) != command.AssignedQuantity ||
            (!order.Unit.AllowsDecimals && decimal.Truncate(command.AssignedQuantity) != command.AssignedQuantity) ||
            order.Batches.Sum(x => x.AssignedQuantity) + command.AssignedQuantity > order.AuthorizedQuantity)
            return Invalid("La cantidad del lote supera lo autorizado disponible.");
        var id = Guid.NewGuid();
        var number = $"{order.Number}-L{order.Batches.Count + 1:000}";
        var lot = new ProductLot { ProductId = order.ProductId, Number = number,
            NormalizedNumber = number.ToUpperInvariant(), CreatedAt = timeProvider.GetUtcNow() };
        var batch = new ProductionBatch { Id = id, CreateOperationId = command.OperationId,
            CreateFingerprint = fp, WorkOrderId = order.Id, Number = number,
            AssignedQuantity = command.AssignedQuantity, FinishedProductLot = lot,
            CreatedByUserId = user.Id, CreatedAt = timeProvider.GetUtcNow() };
        db.ProductionBatches.Add(batch);
        order.Version++;
        try
        {
            await db.SaveChangesAsync(token);
            return new(true, batch.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(false, Errors: ["La orden cambió durante la creación del lote."], Conflict: true);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            prior = await db.ProductionBatches.AsNoTracking().SingleOrDefaultAsync(x => x.CreateOperationId == command.OperationId, token);
            return prior?.CreateFingerprint == fp ? new(true, prior.Id) : new(false, Conflict: true);
        }
    }

    public async Task<ProductionTraceabilityResult> RecordResultAsync(RecordBatchResultCommand command,
        CancellationToken token = default)
    {
        var fp = Fingerprint(command with { Pin = "" });
        var prior = await db.ProductionBatchResults.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.RequestFingerprint == fp ? new(true, prior.Id) : new(false, Conflict: true);
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code is not ("ADMIN" or "OPERATOR")) return Invalid("NIP inválido.");
        if (command.InputQuantity <= 0 || command.GoodQuantity < 0 || command.ReworkQuantity < 0 || command.ScrapQuantity < 0 ||
            command.GoodQuantity + command.ReworkQuantity + command.ScrapQuantity != command.InputQuantity)
            return Invalid("Bueno, retrabajo y merma deben sumar la cantidad procesada.");
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var order = await db.ProductionWorkOrders.Include(x => x.Unit).Include(x => x.Stages).Include(x => x.Events)
                .Include(x => x.Batches).ThenInclude(x => x.Results)
                .Include(x => x.MaterialPlan).SingleOrDefaultAsync(x => x.Id == command.WorkOrderId, token);
            var batch = order?.Batches.SingleOrDefault(x => x.Id == command.BatchId);
            var stage = order?.Stages.SingleOrDefault(x => x.Id == command.StageId);
            if (order is null || batch is null || stage is null || order.Version != command.ExpectedOrderVersion)
                return await Abort(tx, new(false, Errors: ["La orden cambió o el lote ya no está disponible."], Conflict: true), token);
            if (order.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress))
                return await Abort(tx, Invalid("La orden no está disponible para producción."), token);
            if (!await db.ProductionShifts.AnyAsync(x => x.Id == command.ShiftId && x.IsActive, token))
                return await Abort(tx, Invalid("Selecciona un turno activo."), token);
            if (new[] { command.InputQuantity, command.GoodQuantity, command.ReworkQuantity, command.ScrapQuantity }
                    .Any(x => decimal.Round(x, 4) != x) ||
                (!order.Unit.AllowsDecimals && new[] { command.InputQuantity, command.GoodQuantity, command.ReworkQuantity, command.ScrapQuantity }
                    .Any(x => decimal.Truncate(x) != x)))
                return await Abort(tx, Invalid("Las cantidades no son válidas para la unidad del producto."), token);
            var reversedOperationIds = order.Events.Where(x => x.Type == ProductionEventType.ResultReversed && x.RelatedEventId.HasValue)
                .Select(x => order.Events.Single(y => y.Id == x.RelatedEventId).OperationId).ToHashSet();
            var effectiveResults = batch.Results.Where(x => !reversedOperationIds.Contains(x.OperationId)).ToArray();
            var processed = effectiveResults.Where(x => x.WorkOrderStageId == stage.Id && !x.IsRework).Sum(x => x.InputQuantity);
            var rework = effectiveResults.Where(x => x.WorkOrderStageId == stage.Id).Sum(x => x.ReworkQuantity) -
                         effectiveResults.Where(x => x.WorkOrderStageId == stage.Id && x.IsRework).Sum(x => x.InputQuantity);
            var stageInput = stage.Sequence == 1
                ? batch.AssignedQuantity
                : order.Events.Where(x => x.BatchId == batch.Id && x.RelatedStageId == stage.Id && x.Type == ProductionEventType.Received)
                    .Sum(x => x.Quantity);
            var available = command.IsRework ? rework : stageInput - processed;
            if (command.InputQuantity > available) return await Abort(tx, Invalid("La cantidad excede lo disponible en el lote para este proceso."), token);
            var planned = order.MaterialPlan.Where(x => x.WorkOrderStageId == stage.Id).ToArray();
            var requested = command.Materials.Where(x => x.Quantity > 0).GroupBy(x => x.IssueLinkId)
                .Select(x => new ProductionMaterialSelection(x.Key, x.Sum(y => y.Quantity))).ToArray();
            var issued = requested.Length == 0 ? [] : await db.ProductionMaterialIssueLinks
                .Include(x => x.InventoryMovementLine).Where(x => requested.Select(y => y.IssueLinkId).Contains(x.Id)).ToArrayAsync(token);
            var actualByProduct = issued.GroupBy(x => x.InventoryMovementLine.ProductId)
                .ToDictionary(x => x.Key, x => x.Sum(y => requested.Single(r => r.IssueLinkId == y.Id).Quantity));
            var differs = !command.IsRework && (planned.Any(x => decimal.Round(x.PlannedQuantity * command.InputQuantity / order.AuthorizedQuantity, 4) != actualByProduct.GetValueOrDefault(x.MaterialProductId)) ||
                          actualByProduct.Keys.Any(x => planned.All(p => p.MaterialProductId != x)));
            if (differs && string.IsNullOrWhiteSpace(command.DifferenceReason))
                return await Abort(tx, Invalid("El consumo difiere de la receta. Indica el motivo."), token);
            ProductionMaterialOperation? materialOperation = null;
            if (requested.Length > 0)
            {
                var applied = await materialService.ApplyAsync(new ProductionMaterialCommand(command.OperationId,
                    order.Id, stage.Id, order.Version, ProductionMaterialOperationType.Consumption, requested,
                    command.Pin, Notes: command.DifferenceReason), token);
                if (applied.Status != ProductionMaterialStatus.Success)
                    return await Abort(tx, Invalid(applied.Errors?.FirstOrDefault() ?? "No fue posible consumir los materiales."), token);
                materialOperation = await db.ProductionMaterialOperations.Include(x => x.Lines)
                    .ThenInclude(x => x.InventoryMovementLine).ThenInclude(x => x.BalanceChanges)
                    .SingleAsync(x => x.Id == applied.OperationId, token);
                await db.Entry(order).ReloadAsync(token);
            }
            var result = new ProductionBatchResult { OperationId = command.OperationId, RequestFingerprint = fp,
                BatchId = batch.Id, WorkOrderStageId = stage.Id, ShiftId = command.ShiftId,
                ResponsibleUserId = user.Id, IsRework = command.IsRework, InputQuantity = command.InputQuantity,
                GoodQuantity = command.GoodQuantity, ReworkQuantity = command.ReworkQuantity,
                ScrapQuantity = command.ScrapQuantity, DifferenceReason = Normalize(command.DifferenceReason),
                RecordedAt = timeProvider.GetUtcNow() };
            if (materialOperation is not null)
                foreach (var line in materialOperation.Lines)
                    foreach (var change in line.InventoryMovementLine.BalanceChanges.Where(x => x.DeltaQuantity < 0 && x.LotId.HasValue))
                        result.Materials.Add(new ProductionBatchMaterialConsumption { IssueLinkId = line.IssueLinkId,
                            MaterialLotId = change.LotId!.Value, Quantity = -change.DeltaQuantity });
            db.ProductionBatchResults.Add(result);
            order.Status = ProductionWorkOrderStatus.InProgress;
            db.ProductionEvents.Add(new ProductionEvent { OperationId = command.OperationId, RequestFingerprint = fp,
                WorkOrderId = order.Id, WorkOrderStageId = stage.Id, ResponsibleUserId = user.Id,
                BatchId = batch.Id,
                ShiftId = command.ShiftId, Type = command.IsRework ? ProductionEventType.Reworked : ProductionEventType.Processed,
                Quantity = command.InputQuantity, GoodQuantity = command.GoodQuantity,
                ReworkQuantity = command.ReworkQuantity, ScrapQuantity = command.ScrapQuantity,
                Reason = Normalize(command.DifferenceReason), RecordedAt = timeProvider.GetUtcNow() });
            order.Version++;
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            return new(true, result.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await Abort(tx, new(false, Errors: ["La orden cambió durante la confirmación."], Conflict: true), token);
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(token);
            db.ChangeTracker.Clear();
            prior = await db.ProductionBatchResults.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            return prior?.RequestFingerprint == fp ? new(true, prior.Id) : new(false, Conflict: true);
        }
    }

    public async Task<IReadOnlyList<ProductionBatchView>> GetBatchesAsync(Guid orderId, CancellationToken token = default)
    {
        var batches = await db.ProductionBatches.AsNoTracking()
            .Include(x => x.WorkOrder).ThenInclude(x => x.Stages)
            .Include(x => x.WorkOrder).ThenInclude(x => x.Events)
            .Include(x => x.Results).ThenInclude(x => x.WorkOrderStage)
            .Where(x => x.WorkOrderId == orderId).OrderBy(x => x.Number).ToListAsync(token);
        return batches.Select(x =>
        {
            var reversedOperationIds = x.WorkOrder.Events.Where(e => e.Type == ProductionEventType.ResultReversed && e.RelatedEventId.HasValue)
                .Select(e => x.WorkOrder.Events.Single(o => o.Id == e.RelatedEventId).OperationId).ToHashSet();
            var results = x.Results.Where(r => !reversedOperationIds.Contains(r.OperationId)).ToArray();
            var finalStageId = x.WorkOrder.Stages.OrderBy(s => s.Sequence).Last().Id;
            var finalResults = results.Where(r => r.WorkOrderStageId == finalStageId).ToArray();
            var received = x.WorkOrder.Events.Where(e => e.BatchId == x.Id && e.Type == ProductionEventType.WarehouseReceived)
                .Sum(e => e.Quantity);
            return new ProductionBatchView(x.Id, x.Number, x.AssignedQuantity,
                results.Where(r => !r.IsRework).Sum(r => r.InputQuantity), finalResults.Sum(r => r.GoodQuantity),
                results.Sum(r => r.ReworkQuantity) - results.Where(r => r.IsRework).Sum(r => r.InputQuantity),
                results.Sum(r => r.ScrapQuantity), received,
                results.OrderByDescending(r => r.RecordedAt).Select(r => r.WorkOrderStage.Name).FirstOrDefault() ?? "Pendiente",
                x.FinishedProductLotId);
        }).ToArray();
    }

    public async Task<IReadOnlyList<ProductionBatchResultView>> GetResultsAsync(Guid batchId, CancellationToken token = default)
    {
        var results = await db.ProductionBatchResults.AsNoTracking().Where(x => x.BatchId == batchId)
            .Include(x => x.WorkOrderStage).Include(x => x.Shift).Include(x => x.ResponsibleUser)
            .OrderByDescending(x => x.RecordedAt).ToListAsync(token);
        var operationIds = results.Select(x => x.OperationId).ToArray();
        var originalEvents = await db.ProductionEvents.AsNoTracking().Where(x => operationIds.Contains(x.OperationId))
            .ToDictionaryAsync(x => x.Id, x => x.OperationId, token);
        var reversed = await db.ProductionEvents.AsNoTracking().Where(x => x.Type == ProductionEventType.ResultReversed && x.RelatedEventId.HasValue && originalEvents.Keys.Contains(x.RelatedEventId.Value))
            .Select(x => x.RelatedEventId!.Value).ToListAsync(token);
        var reversedOperations = reversed.Select(x => originalEvents[x]).ToHashSet();
        return results.Select(x => new ProductionBatchResultView(x.Id, x.BatchId, x.WorkOrderStage.Name, x.Shift.Name,
            x.IsRework, x.InputQuantity, x.GoodQuantity, x.ReworkQuantity, x.ScrapQuantity,
            x.ResponsibleUser.FullName, x.RecordedAt, !reversedOperations.Contains(x.OperationId))).ToArray();
    }

    public async Task<ProductionTraceabilityResult> ReverseResultAsync(Guid operationId, Guid resultId,
        uint expectedVersion, string reason, string pin, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(pin, token);
        if (user?.Role.Code != "ADMIN") return Invalid("NIP ADMIN inválido.");
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(reason)) return Invalid("Indica el motivo del reverso.");
        var fingerprint = Fingerprint(new { resultId, reason = reason.Trim(), user.Id });
        var prior = await db.ProductionEvents.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operationId, token);
        if (prior is not null) return prior.RequestFingerprint == fingerprint ? new(true, prior.Id) : new(false, Conflict: true);
        var ownsTransaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(token) : null;
        var activeTransaction = transaction ?? db.Database.CurrentTransaction;
        try
        {
            var result = await db.ProductionBatchResults.Include(x => x.Batch).ThenInclude(x => x.WorkOrder).ThenInclude(x => x.Events)
                .Include(x => x.Batch).ThenInclude(x => x.WorkOrder).ThenInclude(x => x.Stages)
                .SingleOrDefaultAsync(x => x.Id == resultId, token);
            if (result is null) return await Abort(activeTransaction, Invalid("El resultado no existe."), token);
            var order = result.Batch.WorkOrder;
            if (order.Version != expectedVersion) return await Abort(activeTransaction, new(false, Errors: ["La orden cambió."], Conflict: true), token);
            var originalEvent = order.Events.SingleOrDefault(x => x.OperationId == result.OperationId && x.Type is ProductionEventType.Processed or ProductionEventType.Reworked);
            if (originalEvent is null || order.Events.Any(x => x.Type == ProductionEventType.ResultReversed && x.RelatedEventId == originalEvent.Id))
                return await Abort(activeTransaction, Invalid("El resultado no existe o ya fue revertido."), token);
            if (order.Events.Any(x => x.BatchId == result.BatchId && x.OperationId != result.OperationId &&
                                      x.RecordedAt >= result.RecordedAt && x.Type != ProductionEventType.ResultReversed))
                return await Abort(activeTransaction, Invalid("Resuelve primero los resultados, entregas o recepciones posteriores de este lote."), token);
            var materialOperation = await db.ProductionMaterialOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == result.OperationId, token);
            if (materialOperation is not null)
            {
                var materialReversal = await materialService.ReverseAsync(operationId, materialOperation.Id, order.Version, pin, reason, token);
                if (materialReversal.Status != ProductionMaterialStatus.Success)
                    return await Abort(activeTransaction, Invalid(materialReversal.Errors?.FirstOrDefault() ?? "No fue posible revertir el consumo de material."), token);
                await db.Entry(order).ReloadAsync(token);
            }
            var reversal = new ProductionEvent { OperationId = operationId, RequestFingerprint = fingerprint,
                WorkOrderId = order.Id, WorkOrderStageId = result.WorkOrderStageId, BatchId = result.BatchId,
                RelatedEventId = originalEvent.Id, Type = ProductionEventType.ResultReversed,
                ResponsibleUserId = user.Id, Quantity = result.InputQuantity, GoodQuantity = result.GoodQuantity,
                ReworkQuantity = result.ReworkQuantity, ScrapQuantity = result.ScrapQuantity,
                Reason = reason.Trim(), RecordedAt = timeProvider.GetUtcNow() };
            db.ProductionEvents.Add(reversal); order.Version++;
            await db.SaveChangesAsync(token);
            if (ownsTransaction && activeTransaction is not null) await activeTransaction.CommitAsync(token);
            return new(true, reversal.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await Abort(activeTransaction, new(false, Errors: ["La orden cambió durante el reverso."], Conflict: true), token);
        }
    }

    public async Task<IReadOnlyList<ProductionMaterialPlanView>> GetPlanAsync(Guid orderId, CancellationToken token = default)
    {
        var plans = await db.ProductionOrderMaterialPlans.AsNoTracking().Include(x => x.MaterialProduct).ThenInclude(x => x.BaseUnit)
            .Include(x => x.WorkOrderStage).Where(x => x.WorkOrderId == orderId).ToListAsync(token);
        var issues = await materialService.GetIssuesAsync(orderId, token);
        return plans.Select(x => { var rows = issues.Where(i => i.StageId == x.WorkOrderStageId && i.ProductSku == x.MaterialProduct.Sku).ToArray();
            var issued = rows.Sum(i => i.Issued); var consumed = rows.Sum(i => i.Consumed); var returned = rows.Sum(i => i.WarehouseReturned + i.SupplierReturned);
            return new ProductionMaterialPlanView(x.Id, x.WorkOrderStageId, x.WorkOrderStage.Name, x.MaterialProductId,
                x.MaterialProduct.Sku, x.MaterialProduct.Description ?? "", x.MaterialProduct.BaseUnit.Code,
                x.PlannedQuantity, issued, consumed, returned, Math.Max(0, x.PlannedQuantity - issued),
                consumed != x.PlannedQuantity); }).OrderBy(x => x.Stage).ThenBy(x => x.Sku).ToArray();
    }

    public Task<List<ProductionRecipe>> GetRecipesAsync(CancellationToken token = default) => db.ProductionRecipes.AsNoTracking()
        .Include(x => x.Product).Include(x => x.Lines).ThenInclude(x => x.MaterialProduct).ThenInclude(x => x.BaseUnit)
        .Include(x => x.Lines).ThenInclude(x => x.Stage).Where(x => x.IsActive).OrderBy(x => x.Product.Sku).ToListAsync(token);

    public async Task<ProductionProductConfigurationView?> GetProductConfigurationAsync(Guid productId,
        CancellationToken token = default)
    {
        var product = await db.Products.AsNoTracking().Where(x => x.Id == productId)
            .Select(x => new { x.Id, x.Sku, x.Description, x.IsActive }).SingleOrDefaultAsync(token);
        if (product is null) return null;

        var route = await db.ProductionRoutes.AsNoTracking().Where(x => x.ProductId == productId && x.IsActive)
            .Include(x => x.Stages).ThenInclude(x => x.Stage).SingleOrDefaultAsync(token);
        var recipe = await db.ProductionRecipes.AsNoTracking().Where(x => x.ProductId == productId && x.IsActive)
            .Include(x => x.Lines).ThenInclude(x => x.MaterialProduct).ThenInclude(x => x.BaseUnit)
            .Include(x => x.Lines).ThenInclude(x => x.Stage).SingleOrDefaultAsync(token);

        var stages = route?.Stages.OrderBy(x => x.Sequence)
            .Select(x => new ProductionProductStageView(x.StageId, x.Sequence, x.Stage.Code, x.Stage.Name)).ToArray()
            ?? [];
        var recipeView = recipe is null ? null : new ProductionRecipeView(recipe.Id, product.Id,
            $"{product.Sku} · {product.Description}", recipe.Version, recipe.BaseQuantity,
            recipe.Lines.OrderBy(x => stages.FirstOrDefault(stage => stage.Id == x.StageId)?.Sequence ?? int.MaxValue)
                .ThenBy(x => x.MaterialProduct.Sku)
                .Select(x => new ProductionRecipeLineView(x.MaterialProductId,
                    $"{x.MaterialProduct.Sku} · {x.MaterialProduct.Description}", x.StageId, x.Stage.Name,
                    x.Quantity, x.MaterialProduct.BaseUnit.Code)).ToArray());
        return new ProductionProductConfigurationView(product.IsActive, route?.Id, route?.Name, stages, recipeView);
    }

    public async Task<IReadOnlyList<ProductionTraceLinkView>> GetTraceAsync(Guid batchId, CancellationToken token = default) =>
        await db.ProductionBatchMaterialConsumptions.AsNoTracking().Where(x => x.BatchResult.BatchId == batchId &&
                !db.ProductionEvents.Any(e => e.Type == ProductionEventType.ResultReversed && e.RelatedEvent!.OperationId == x.BatchResult.OperationId))
            .OrderBy(x => x.BatchResult.RecordedAt).Select(x => new ProductionTraceLinkView(x.BatchResult.Batch.Number,
                x.BatchResult.Batch.FinishedProductLot.Number, x.BatchResult.Batch.WorkOrder.Product.Sku,
                x.MaterialLot.Number, x.IssueLink.InventoryMovementLine.Product.Sku, x.Quantity,
                x.IssueLink.InventoryMovementLine.Product.BaseUnit.Code, x.BatchResult.WorkOrderStage.Name,
                x.BatchResult.ResponsibleUser.FullName, x.BatchResult.RecordedAt)).ToListAsync(token);

    public async Task<IReadOnlyList<ProductionTraceLinkView>> SearchTraceAsync(string? search, CancellationToken token = default)
    {
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (search is null) return [];
        return await db.ProductionBatchMaterialConsumptions.AsNoTracking()
            .Where(x => !db.ProductionEvents.Any(e => e.Type == ProductionEventType.ResultReversed && e.RelatedEvent!.OperationId == x.BatchResult.OperationId) &&
                        (x.BatchResult.Batch.Number.Contains(search) ||
                        x.BatchResult.Batch.FinishedProductLot.Number.Contains(search) ||
                        x.MaterialLot.Number.Contains(search) ||
                        x.BatchResult.Batch.WorkOrder.Product.Sku.Contains(search) ||
                        x.IssueLink.InventoryMovementLine.Product.Sku.Contains(search)))
            .OrderByDescending(x => x.BatchResult.RecordedAt).Take(200)
            .Select(x => new ProductionTraceLinkView(x.BatchResult.Batch.Number,
                x.BatchResult.Batch.FinishedProductLot.Number, x.BatchResult.Batch.WorkOrder.Product.Sku,
                x.MaterialLot.Number, x.IssueLink.InventoryMovementLine.Product.Sku, x.Quantity,
                x.IssueLink.InventoryMovementLine.Product.BaseUnit.Code, x.BatchResult.WorkOrderStage.Name,
                x.BatchResult.ResponsibleUser.FullName, x.BatchResult.RecordedAt)).ToListAsync(token);
    }

    public async Task<IReadOnlyList<ProductionDeliveryView>> GetPendingDeliveriesAsync(Guid orderId, CancellationToken token = default)
    {
        var events = await db.ProductionEvents.AsNoTracking().Where(x => x.WorkOrderId == orderId && x.BatchId != null).ToListAsync(token);
        var batchNumbers = await db.ProductionBatches.AsNoTracking().Where(x => x.WorkOrderId == orderId).ToDictionaryAsync(x => x.Id, x => x.Number, token);
        return events.Where(x => x.Type == ProductionEventType.Delivered).Select(x => new ProductionDeliveryView(x.Id,
            x.BatchId!.Value, batchNumbers.GetValueOrDefault(x.BatchId.Value) ?? "Lote", x.WorkOrderStageId!.Value,
            x.RelatedStageId!.Value, x.Quantity,
            x.Quantity - events.Where(y => y.RelatedEventId == x.Id && y.Type is ProductionEventType.Received or ProductionEventType.DifferenceReturned or ProductionEventType.DifferenceLost).Sum(y => y.Quantity),
            x.RecordedAt)).Where(x => x.Pending > 0).OrderBy(x => x.RecordedAt).ToArray();
    }

    private static ProductionTraceabilityResult Invalid(string error) => new(false, Errors: [error]);
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static async Task<ProductionTraceabilityResult> Abort(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx,
        ProductionTraceabilityResult result, CancellationToken token) { if (tx is not null) await tx.RollbackAsync(token); return result; }
}
