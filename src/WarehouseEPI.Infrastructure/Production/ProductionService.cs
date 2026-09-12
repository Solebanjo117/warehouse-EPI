using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public sealed class ProductionService(WarehouseDbContext db, UserPinService pins, InventoryMovementService movements, TimeProvider timeProvider)
{
    public async Task<ProductionCommandResult> CreateRouteAsync(CreateProductionRouteCommand command, CancellationToken token = default)
    {
        var user = await AdminAsync(command.Pin, token); if (user is null) return new(ProductionCommandStatus.InvalidPin);
        var name = command.Name.Trim(); var ids = command.StageIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (name.Length is < 1 or > 120 || ids.Length == 0) return Invalid("Indica un nombre y al menos una etapa.");
        if (!await db.Products.AnyAsync(x => x.Id == command.ProductId && x.IsActive, token)) return Invalid("El producto no existe o está inactivo.");
        if (await db.ProductionRoutes.AnyAsync(x => x.ProductId == command.ProductId && x.IsActive, token)) return Invalid("El producto ya tiene una ruta activa.");
        if (await db.ProductionStages.CountAsync(x => ids.Contains(x.Id) && x.IsActive, token) != ids.Length) return Invalid("Una etapa no existe o está inactiva.");
        var route = new ProductionRoute { ProductId = command.ProductId, Name = name, CreatedAt = timeProvider.GetUtcNow() };
        for (var i=0;i<ids.Length;i++) route.Stages.Add(new ProductionRouteStage { StageId=ids[i], Sequence=i+1 });
        db.ProductionRoutes.Add(route); await db.SaveChangesAsync(token); return new(ProductionCommandStatus.Success);
    }

    public async Task<ProductionCommandResult> CreateOrderAsync(CreateProductionOrderCommand command, CancellationToken token = default)
    {
        var fingerprint = Fingerprint(command with { Pin = "" });
        var existing = await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x => x.CreateOperationId == command.OperationId, token);
        if (existing is not null) return existing.CreateFingerprint == fingerprint ? new(ProductionCommandStatus.Success, existing.Id) : new(ProductionCommandStatus.IdempotencyConflict);
        var user = await AdminAsync(command.Pin, token); if (user is null) return new(ProductionCommandStatus.InvalidPin);
        if (command.TargetQuantity <= 0 || decimal.Round(command.TargetQuantity,4) != command.TargetQuantity) return Invalid("La cantidad objetivo debe ser positiva y admitir hasta cuatro decimales.");
        var product = await db.Products.Include(x=>x.BaseUnit).SingleOrDefaultAsync(x=>x.Id==command.ProductId && x.IsActive,token);
        if (product is null) return Invalid("El producto no existe o está inactivo.");
        if (!product.BaseUnit.AllowsDecimals && decimal.Truncate(command.TargetQuantity)!=command.TargetQuantity) return Invalid("La unidad del producto no permite decimales.");
        var route = await db.ProductionRoutes.Include(x=>x.Stages).ThenInclude(x=>x.Stage).SingleOrDefaultAsync(x=>x.ProductId==command.ProductId && x.IsActive,token);
        if (route is null || route.Stages.Count==0) return Invalid("Configura una ruta activa para el producto antes de crear la orden.");
        var recipe = await db.ProductionRecipes.Include(x => x.Lines).ThenInclude(x => x.MaterialProduct).SingleOrDefaultAsync(x => x.ProductId == command.ProductId && x.IsActive, token);
        var id=Guid.NewGuid(); var number=$"OT-{timeProvider.GetUtcNow():yyyy}-{id.ToString("N")[..6].ToUpperInvariant()}";
        var order=new ProductionWorkOrder { Id=id, CreateOperationId=command.OperationId, CreateFingerprint=fingerprint, Number=number, ExternalReference=Trim(command.ExternalReference,120), ProductId=product.Id, UnitId=product.BaseUnitId, TargetQuantity=command.TargetQuantity, AuthorizedQuantity=command.TargetQuantity, DueDate=command.DueDate, Notes=Trim(command.Notes,500), CreatedByUserId=user.Id, CreatedAt=timeProvider.GetUtcNow(), UsesBatchTraceability=recipe is not null, RecipeVersion=recipe?.Version };
        foreach(var item in route.Stages.OrderBy(x=>x.Sequence)) order.Stages.Add(new ProductionWorkOrderStage { SourceStageId=item.StageId, Sequence=item.Sequence, Code=item.Stage.Code, Name=item.Stage.Name });
        if(recipe is not null)
        {
            foreach(var line in recipe.Lines)
            {
                var stage=order.Stages.Single(x=>x.SourceStageId==line.StageId);
                var planned=decimal.Round(line.Quantity*command.TargetQuantity/recipe.BaseQuantity,4,MidpointRounding.AwayFromZero);
                order.MaterialPlan.Add(new ProductionOrderMaterialPlan { WorkOrderStage=stage, MaterialProductId=line.MaterialProductId, UnitId=line.MaterialProduct.BaseUnitId, PlannedQuantity=planned, OriginalPlannedQuantity=planned });
            }
        }
        order.Events.Add(Event(command.OperationId,fingerprint,order,user,ProductionEventType.Created,reason:"Orden creada en borrador."));
        db.ProductionWorkOrders.Add(order);
        try { await db.SaveChangesAsync(token); return new(ProductionCommandStatus.Success,order.Id); }
        catch(DbUpdateException) { db.ChangeTracker.Clear(); existing=await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x=>x.CreateOperationId==command.OperationId,token); return existing?.CreateFingerprint==fingerprint?new(ProductionCommandStatus.Success,existing.Id):new(ProductionCommandStatus.IdempotencyConflict); }
    }

    public Task<ProductionCommandResult> ReleaseAsync(ProductionOrderActionCommand command,CancellationToken token=default)=>AdminStateAsync(command,ProductionWorkOrderStatus.Draft,ProductionWorkOrderStatus.Released,ProductionEventType.Released,"La orden sólo se puede liberar desde Borrador.",token);
    public Task<ProductionCommandResult> PauseAsync(ProductionOrderActionCommand command,CancellationToken token=default)=>AdminStateAsync(command,null,ProductionWorkOrderStatus.Paused,ProductionEventType.Paused,"Sólo se puede pausar una orden liberada o en proceso.",token,ProductionWorkOrderStatus.Released,ProductionWorkOrderStatus.InProgress);
    public Task<ProductionCommandResult> ResumeAsync(ProductionOrderActionCommand command,CancellationToken token=default)=>AdminStateAsync(command,ProductionWorkOrderStatus.Paused,ProductionWorkOrderStatus.InProgress,ProductionEventType.Resumed,"La orden no está pausada.",token);
    public async Task<ProductionCommandResult> CancelAsync(ProductionOrderActionCommand command,CancellationToken token=default)
    {
        var user=await AdminAsync(command.Pin,token);if(user is null)return new(ProductionCommandStatus.InvalidPin);
        if(string.IsNullOrWhiteSpace(command.Reason))return Invalid("Indica el motivo de la cancelación.");var fp=Fingerprint(command with{Pin=""});
        return await MutateAsync(command.OperationId,command.WorkOrderId,fp,async order=>{if(command.ExpectedVersion.HasValue&&order.Version!=command.ExpectedVersion)return new(ProductionCommandStatus.ConcurrencyConflict);if(order.Status!=ProductionWorkOrderStatus.Draft)return Invalid("Sólo se puede cancelar una orden en borrador.");order.Status=ProductionWorkOrderStatus.Cancelled;order.Events.Add(Event(command.OperationId,fp,order,user,ProductionEventType.Cancelled,reason:command.Reason));await Task.CompletedTask;return Success(order.Id);},token);
    }

    public async Task<ProductionCommandResult> AuthorizeAdditionalAsync(ProductionOrderActionCommand command,CancellationToken token=default)
    {
        var user=await AdminAsync(command.Pin,token); if(user is null)return new(ProductionCommandStatus.InvalidPin);
        if(command.Quantity<=0 || string.IsNullOrWhiteSpace(command.Reason))return Invalid("Indica una cantidad adicional positiva y el motivo.");
        return await MutateAsync(command.OperationId,command.WorkOrderId,Fingerprint(command with{Pin=""}),async order=>{
            if(command.ExpectedVersion.HasValue&&order.Version!=command.ExpectedVersion)return new(ProductionCommandStatus.ConcurrencyConflict);
            if(order.Status is ProductionWorkOrderStatus.Draft or ProductionWorkOrderStatus.Closed or ProductionWorkOrderStatus.Cancelled)return Invalid("La orden no admite ampliaciones en su estado actual.");
            order.AuthorizedQuantity+=command.Quantity; order.Events.Add(Event(command.OperationId,Fingerprint(command with{Pin=""}),order,user,ProductionEventType.QuantityAuthorized,quantity:command.Quantity,reason:command.Reason)); await Task.CompletedTask; return Success(order.Id);
        },token);
    }

    public async Task<ProductionCommandResult> ProcessAsync(RecordProductionCommand command,CancellationToken token=default)
    {
        var user=await OperatorAsync(command.Pin,token); if(user is null)return new(ProductionCommandStatus.InvalidPin);
        if(command.InputQuantity<=0 || command.GoodQuantity<0 || command.ReworkQuantity<0 || command.ScrapQuantity<0 || command.GoodQuantity+command.ReworkQuantity+command.ScrapQuantity!=command.InputQuantity)return Invalid("La cantidad buena, retrabajo y merma deben sumar la cantidad procesada.");
        var fp=Fingerprint(command with{Pin=""});
        return await MutateAsync(command.OperationId,command.WorkOrderId,fp,async order=>{
            if(order.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress))return Invalid("La orden no está disponible para producción.");
            var stage=order.Stages.SingleOrDefault(x=>x.Id==command.StageId); if(stage is null)return Invalid("La etapa no pertenece a la orden.");
            if(!await db.ProductionShifts.AnyAsync(x=>x.Id==command.ShiftId&&x.IsActive,token))return Invalid("El turno no existe o está inactivo.");
            var progress=BuildProgress(order).Single(x=>x.Id==stage.Id); var available=command.IsRework?progress.Rework:progress.AvailableInput;
            if(command.InputQuantity>available)return Invalid(command.IsRework?"La cantidad excede el retrabajo pendiente.":"La cantidad excede el material disponible en la etapa.");
            order.Status=ProductionWorkOrderStatus.InProgress; order.Events.Add(Event(command.OperationId,fp,order,user,command.IsRework?ProductionEventType.Reworked:ProductionEventType.Processed,stage.Id,null,command.ShiftId,command.InputQuantity,command.GoodQuantity,command.ReworkQuantity,command.ScrapQuantity,command.Reason)); return Success(order.Id);
        },token);
    }

    public Task<ProductionCommandResult> DeliverAsync(ProductionHandoffCommand c,CancellationToken t=default)=>HandoffAsync(c,ProductionEventType.Delivered,t);
    public Task<ProductionCommandResult> ReceiveAsync(ProductionHandoffCommand c,CancellationToken t=default)=>HandoffAsync(c,ProductionEventType.Received,t);
    public Task<ProductionCommandResult> ReturnDifferenceAsync(ProductionHandoffCommand c,CancellationToken t=default)=>HandoffAsync(c,ProductionEventType.DifferenceReturned,t,true);
    public Task<ProductionCommandResult> LoseDifferenceAsync(ProductionHandoffCommand c,CancellationToken t=default)=>HandoffAsync(c,ProductionEventType.DifferenceLost,t,true,true);

    public async Task<ProductionCommandResult> ReceiveWarehouseAsync(ReceiveProductionToWarehouseCommand command,CancellationToken token=default)
    {
        var user=await OperatorAsync(command.Pin,token); if(user is null)return new(ProductionCommandStatus.InvalidPin);
        if(command.Quantity<=0)return Invalid("La cantidad debe ser positiva."); var fp=Fingerprint(command with{Pin=""});
        var prior=await ExistingAsync(command.OperationId,fp,token); if(prior is not null)return prior;
        await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(token):null;
        try{
            var order=await LoadOrderAsync(command.WorkOrderId,token); if(order is null)return await AbortAsync(tx,new(ProductionCommandStatus.NotFound),token);
            if(command.ExpectedVersion.HasValue&&order.Version!=command.ExpectedVersion)return await AbortAsync(tx,new(ProductionCommandStatus.ConcurrencyConflict),token);
            if(order.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress))return await AbortAsync(tx,Invalid("La orden no admite recepción final."),token);
            var final=order.Stages.OrderBy(x=>x.Sequence).Last(); if(final.Id!=command.FinalStageId)return await AbortAsync(tx,Invalid("La recepción debe provenir de la última etapa."),token);
            ProductionBatch? batch=null;
            decimal available;
            if(order.UsesBatchTraceability)
            {
                if(command.BatchId is not Guid batchId)return await AbortAsync(tx,Invalid("Selecciona el lote de producción."),token);
                batch=await db.ProductionBatches.Include(x=>x.Results).SingleOrDefaultAsync(x=>x.Id==batchId&&x.WorkOrderId==order.Id,token);
                if(batch is null)return await AbortAsync(tx,Invalid("El lote de producción no pertenece a la orden."),token);
                var reversedOperations=order.Events.Where(x=>x.Type==ProductionEventType.ResultReversed&&x.RelatedEventId.HasValue)
                    .Select(x=>order.Events.Single(e=>e.Id==x.RelatedEventId).OperationId).ToHashSet();
                available=batch.Results.Where(x=>x.WorkOrderStageId==final.Id&&!reversedOperations.Contains(x.OperationId)).Sum(x=>x.GoodQuantity)-order.Events.Where(x=>x.BatchId==batch.Id&&x.Type==ProductionEventType.WarehouseReceived).Sum(x=>x.Quantity);
            }
            else available=BuildProgress(order).Single(x=>x.Id==final.Id).AvailableToDeliver;
            if(command.Quantity>available)return await AbortAsync(tx,Invalid("La cantidad excede el producto terminado disponible."),token);
            var movement=await movements.ConfirmAuthorizedAsync(new InventoryMovementCommand(command.OperationId,InventoryMovementType.Entry,command.Pin,[new(order.ProductId,command.Quantity,DestinationLocationId:command.DestinationLocationId,DestinationLotId:batch?.FinishedProductLotId)],order.Number,"Recepción de producción",command.ApprovedSharedAssignments,InventoryMovementPurpose.ProductionReceipt),user,cancellationToken:token);
            if(movement.Status!=InventoryMovementStatus.Success)return await AbortAsync(tx,MapMovement(movement,order.Id),token);
            order.Status=ProductionWorkOrderStatus.InProgress; var receiptEvent=Event(command.OperationId,fp,order,user,ProductionEventType.WarehouseReceived,final.Id,quantity:command.Quantity,movementId:movement.MovementId,batchId:batch?.Id); order.Events.Add(receiptEvent); db.Entry(receiptEvent).State=EntityState.Added; order.Version++; await db.SaveChangesAsync(token); if(tx is not null)await tx.CommitAsync(token); return new(ProductionCommandStatus.Success,order.Id,movement.MovementId);
        }catch(DbUpdateConcurrencyException){return await AbortAsync(tx,new(ProductionCommandStatus.ConcurrencyConflict),token);}catch{if(tx is not null)await tx.RollbackAsync(token);throw;}
    }

    public async Task<ProductionCommandResult> CloseAsync(ProductionOrderActionCommand command,CancellationToken token=default)
    {
        var user=await AdminAsync(command.Pin,token);if(user is null)return new(ProductionCommandStatus.InvalidPin);var fp=Fingerprint(command with{Pin=""});
        return await MutateAsync(command.OperationId,command.WorkOrderId,fp,async order=>{
            if(command.ExpectedVersion.HasValue&&order.Version!=command.ExpectedVersion)return new(ProductionCommandStatus.ConcurrencyConflict);
            if(order.Status is ProductionWorkOrderStatus.Draft or ProductionWorkOrderStatus.Cancelled or ProductionWorkOrderStatus.Closed)return Invalid("La orden no se puede cerrar en su estado actual.");
            var progress=BuildProgress(order); if(progress.Any(x=>x.AvailableInput>0||x.Rework>0||x.PendingReceipt>0||x.AvailableToDeliver>0))return Invalid("Resuelve material disponible, retrabajos, entregas y diferencias antes de cerrar.");
            if(order.UsesBatchTraceability){var reversedMaterial=await db.ProductionMaterialOperations.AsNoTracking().Where(x=>x.ReversesOperationId!=null).Select(x=>x.ReversesOperationId!.Value).ToListAsync(token);var materialLinks=await db.ProductionMaterialIssueLinks.Include(x=>x.InventoryMovementLine).Include(x=>x.OperationLines).ThenInclude(x=>x.Operation).Where(x=>x.WorkOrderId==order.Id).ToListAsync(token);if(materialLinks.Any(x=>x.InventoryMovementLine.Quantity-x.OperationLines.Where(line=>line.Operation.Type!=ProductionMaterialOperationType.Reversal&&!reversedMaterial.Contains(line.Operation.Id)).Sum(line=>line.Quantity)>0))return Invalid("Consume o devuelve todo el material WIP reservado antes de cerrar.");}
            var received=order.Events.Where(x=>x.Type==ProductionEventType.WarehouseReceived).Sum(x=>x.Quantity); if(received!=order.TargetQuantity&&string.IsNullOrWhiteSpace(command.Reason))return Invalid("El cierre con faltante o excedente requiere un motivo.");
            order.Status=ProductionWorkOrderStatus.Closed;order.ClosedAt=timeProvider.GetUtcNow();order.Events.Add(Event(command.OperationId,fp,order,user,ProductionEventType.Closed,quantity:received,reason:command.Reason));await Task.CompletedTask;return Success(order.Id);
        },token);
    }

    private async Task<ProductionCommandResult> HandoffAsync(ProductionHandoffCommand command,ProductionEventType type,CancellationToken token,bool admin=false,bool lost=false)
    {
        var user=admin?await AdminAsync(command.Pin,token):await OperatorAsync(command.Pin,token);if(user is null)return new(ProductionCommandStatus.InvalidPin);
        if(command.Quantity<=0 || ((type is ProductionEventType.DifferenceReturned or ProductionEventType.DifferenceLost)&&string.IsNullOrWhiteSpace(command.Reason)))return Invalid("Indica una cantidad positiva y el motivo cuando concilies una diferencia.");var fp=Fingerprint(command with{Pin=""});
        return await MutateAsync(command.OperationId,command.WorkOrderId,fp,async order=>{
            if(command.ExpectedVersion.HasValue&&order.Version!=command.ExpectedVersion)return new(ProductionCommandStatus.ConcurrencyConflict);
            if(order.Status is not (ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress))return Invalid("La orden no está disponible para captura.");
            var stages=order.Stages.OrderBy(x=>x.Sequence).ToArray();var source=stages.SingleOrDefault(x=>x.Id==command.SourceStageId);var target=stages.SingleOrDefault(x=>x.Id==command.TargetStageId);
            if(source is null||target is null||target.Sequence!=source.Sequence+1)return Invalid("La entrega debe realizarse entre etapas consecutivas.");
            decimal available;
            Guid? relatedEventId=null;
            if(order.UsesBatchTraceability)
            {
                if(command.BatchId is not Guid batchId || !await db.ProductionBatches.AnyAsync(x=>x.Id==batchId&&x.WorkOrderId==order.Id,token))return Invalid("Selecciona un lote válido de la orden.");
                if(type==ProductionEventType.Delivered)
                {
                    var good=await db.ProductionBatchResults.Where(x=>x.BatchId==batchId&&x.WorkOrderStageId==source.Id&&
                        !db.ProductionEvents.Any(e=>e.Type==ProductionEventType.ResultReversed&&e.RelatedEvent!.OperationId==x.OperationId)).SumAsync(x=>x.GoodQuantity,token);
                    var delivered=order.Events.Where(x=>x.BatchId==batchId&&x.WorkOrderStageId==source.Id&&x.Type==ProductionEventType.Delivered).Sum(x=>x.Quantity);
                    available=good-delivered;
                }
                else
                {
                    if(command.DeliveryEventId is not Guid deliveryId)return Invalid("Selecciona la entrega que se recibe o concilia.");
                    var delivery=order.Events.SingleOrDefault(x=>x.Id==deliveryId&&x.BatchId==batchId&&x.Type==ProductionEventType.Delivered&&x.WorkOrderStageId==source.Id&&x.RelatedStageId==target.Id);
                    if(delivery is null)return Invalid("La entrega no pertenece al lote y procesos seleccionados.");
                    available=delivery.Quantity-order.Events.Where(x=>x.RelatedEventId==delivery.Id&&x.Type is ProductionEventType.Received or ProductionEventType.DifferenceReturned or ProductionEventType.DifferenceLost).Sum(x=>x.Quantity);
                    relatedEventId=delivery.Id;
                }
            }
            else {var p=BuildProgress(order).Single(x=>x.Id==source.Id);available=type==ProductionEventType.Delivered?p.AvailableToDeliver:p.PendingReceipt;}
            if(command.Quantity>available)return Invalid(type==ProductionEventType.Delivered?"La cantidad excede lo disponible para entregar.":"La cantidad excede lo pendiente de esta entrega.");
            order.Status=ProductionWorkOrderStatus.InProgress;order.Events.Add(Event(command.OperationId,fp,order,user,type,source.Id,target.Id,quantity:command.Quantity,reason:command.Reason,batchId:command.BatchId,relatedEventId:relatedEventId));await Task.CompletedTask;return Success(order.Id);
        },token);
    }

    private async Task<ProductionCommandResult> AdminStateAsync(ProductionOrderActionCommand c,ProductionWorkOrderStatus? expected,ProductionWorkOrderStatus next,ProductionEventType type,string error,CancellationToken token,params ProductionWorkOrderStatus[] allowed)
    {var user=await AdminAsync(c.Pin,token);if(user is null)return new(ProductionCommandStatus.InvalidPin);var fp=Fingerprint(c with{Pin=""});return await MutateAsync(c.OperationId,c.WorkOrderId,fp,async o=>{if(c.ExpectedVersion.HasValue&&o.Version!=c.ExpectedVersion)return new(ProductionCommandStatus.ConcurrencyConflict);if(expected.HasValue?o.Status!=expected:!allowed.Contains(o.Status))return Invalid(error);o.Status=next;if(next==ProductionWorkOrderStatus.Released)o.ReleasedAt=timeProvider.GetUtcNow();o.Events.Add(Event(c.OperationId,fp,o,user,type,reason:c.Reason));await Task.CompletedTask;return Success(o.Id);},token);}
    private async Task<ProductionCommandResult> MutateAsync(Guid operationId,Guid orderId,string fp,Func<ProductionWorkOrder,Task<ProductionCommandResult>> change,CancellationToken token)
    {var prior=await ExistingAsync(operationId,fp,token);if(prior is not null)return prior;var order=await LoadOrderAsync(orderId,token);if(order is null)return new(ProductionCommandStatus.NotFound);var result=await change(order);if(result.Status!=ProductionCommandStatus.Success)return result;var newEvent=order.Events.Single(x=>x.OperationId==operationId);db.Entry(newEvent).State=EntityState.Added;order.Version++;try{await db.SaveChangesAsync(token);return result;}catch(DbUpdateConcurrencyException ex){var detail=string.Join(", ",ex.Entries.Select(x=>$"{x.Metadata.ClrType.Name}:{x.State}"));db.ChangeTracker.Clear();return new(ProductionCommandStatus.ConcurrencyConflict,Errors:[$"{ex.Message} ({detail})"]);}catch(DbUpdateException){db.ChangeTracker.Clear();return await ExistingAsync(operationId,fp,token)??new(ProductionCommandStatus.IdempotencyConflict);}}
    private Task<ProductionWorkOrder?> LoadOrderAsync(Guid id,CancellationToken t)=>db.ProductionWorkOrders.Include(x=>x.Product).ThenInclude(x=>x.BaseUnit).Include(x=>x.Stages).Include(x=>x.Events).SingleOrDefaultAsync(x=>x.Id==id,t);
    private async Task<ProductionCommandResult?> ExistingAsync(Guid op,string fp,CancellationToken t){var e=await db.ProductionEvents.AsNoTracking().SingleOrDefaultAsync(x=>x.OperationId==op,t);return e is null?null:e.RequestFingerprint==fp?new(ProductionCommandStatus.Success,e.WorkOrderId,e.InventoryMovementId):new(ProductionCommandStatus.IdempotencyConflict);}
    private async Task<User?> OperatorAsync(string pin,CancellationToken t){var u=await pins.AuthenticateAsync(pin,t);return u?.Role.Code is "ADMIN" or "OPERATOR"?u:null;} private async Task<User?> AdminAsync(string pin,CancellationToken t){var u=await pins.AuthenticateAsync(pin,t);return u?.Role.Code=="ADMIN"?u:null;}
    private static ProductionCommandResult Invalid(string e)=>new(ProductionCommandStatus.ValidationFailed,Errors:[e]);private static ProductionCommandResult Success(Guid id)=>new(ProductionCommandStatus.Success,id);
    private static string? Trim(string? value,int max){value=string.IsNullOrWhiteSpace(value)?null:value.Trim();return value?.Length>max?value[..max]:value;}
    private static string Fingerprint<T>(T value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private ProductionEvent Event(Guid op,string fp,ProductionWorkOrder o,User u,ProductionEventType type,Guid? stage=null,Guid? related=null,Guid? shift=null,decimal quantity=0,decimal good=0,decimal rework=0,decimal scrap=0,string? reason=null,Guid? movementId=null,Guid? batchId=null,Guid? relatedEventId=null)=>new(){OperationId=op,RequestFingerprint=fp,WorkOrder=o,WorkOrderStageId=stage,RelatedStageId=related,BatchId=batchId,RelatedEventId=relatedEventId,Type=type,ResponsibleUserId=u.Id,ShiftId=shift,Quantity=quantity,GoodQuantity=good,ReworkQuantity=rework,ScrapQuantity=scrap,Reason=Trim(reason,500),InventoryMovementId=movementId,RecordedAt=timeProvider.GetUtcNow()};
    internal static IReadOnlyList<ProductionStageProgress> BuildProgress(ProductionWorkOrder order)
    {var ordered=order.Stages.OrderBy(x=>x.Sequence).ToArray();var list=new List<ProductionStageProgress>();var reversed=order.Events.Where(x=>x.Type==ProductionEventType.ResultReversed&&x.RelatedEventId.HasValue).Select(x=>x.RelatedEventId!.Value).ToHashSet();var effective=order.Events.Where(x=>!reversed.Contains(x.Id)&&x.Type!=ProductionEventType.ResultReversed).ToArray();foreach(var s in ordered){var ev=effective;var processed=ev.Where(x=>x.WorkOrderStageId==s.Id&&x.Type==ProductionEventType.Processed).Sum(x=>x.Quantity);var good=ev.Where(x=>x.WorkOrderStageId==s.Id&&x.Type is ProductionEventType.Processed or ProductionEventType.Reworked).Sum(x=>x.GoodQuantity);var rework=ev.Where(x=>x.WorkOrderStageId==s.Id&&x.Type is ProductionEventType.Processed or ProductionEventType.Reworked).Sum(x=>x.ReworkQuantity)-ev.Where(x=>x.WorkOrderStageId==s.Id&&x.Type==ProductionEventType.Reworked).Sum(x=>x.Quantity);var scrap=ev.Where(x=>x.WorkOrderStageId==s.Id&&x.Type is ProductionEventType.Processed or ProductionEventType.Reworked).Sum(x=>x.ScrapQuantity);var delivered=ev.Where(x=>x.WorkOrderStageId==s.Id&&x.Type==ProductionEventType.Delivered).Sum(x=>x.Quantity);var returned=ev.Where(x=>x.WorkOrderStageId==s.Id&&x.Type==ProductionEventType.DifferenceReturned).Sum(x=>x.Quantity);var received=ev.Where(x=>x.RelatedStageId==s.Id&&x.Type==ProductionEventType.Received).Sum(x=>x.Quantity);var pending=delivered-ev.Where(x=>x.WorkOrderStageId==s.Id&&x.Type is ProductionEventType.Received or ProductionEventType.DifferenceReturned or ProductionEventType.DifferenceLost).Sum(x=>x.Quantity);var input=s.Sequence==1?order.AuthorizedQuantity:received;var warehouse=s.Sequence==ordered.Length?ev.Where(x=>x.WorkOrderStageId==s.Id&&x.Type==ProductionEventType.WarehouseReceived).Sum(x=>x.Quantity):0;list.Add(new(s.Id,s.Sequence,s.Code,s.Name,input-processed,processed,good,rework,scrap,good-delivered+returned-warehouse,delivered,received,pending));}return list;}
    private static ProductionCommandResult MapMovement(InventoryMovementResult r,Guid id)=>r.Status switch{InventoryMovementStatus.InvalidPin=>new(ProductionCommandStatus.InvalidPin,id),InventoryMovementStatus.RequiresLocationSharingConfirmation=>new(ProductionCommandStatus.RequiresLocationSharingConfirmation,id,Conflicts:r.Conflicts),InventoryMovementStatus.IdempotencyConflict=>new(ProductionCommandStatus.IdempotencyConflict,id),InventoryMovementStatus.BalanceChanged=>new(ProductionCommandStatus.ConcurrencyConflict,id),_=>new(ProductionCommandStatus.ValidationFailed,id,Errors:r.ValidationErrors)};
    private static async Task<ProductionCommandResult> AbortAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx,ProductionCommandResult r,CancellationToken t){if(tx is not null)await tx.RollbackAsync(t);return r;}
}
