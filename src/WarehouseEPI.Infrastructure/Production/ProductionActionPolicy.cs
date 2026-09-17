using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionActionOption(string Code, string Label, bool RequiresAdmin);

public sealed record ProductionBlockingTask(string Message,string Task,Guid? StageId=null,string? Url=null);

public static class ProductionActionPolicy
{
    public static IReadOnlyList<ProductionBlockingTask> ClosureProgress(IReadOnlyList<ProductionStageProgress> stages)
    {
        var result=new List<ProductionBlockingTask>();
        foreach(var stage in stages) {
            if(stage.AvailableInput>0)result.Add(new($"{stage.Name}: {stage.AvailableInput:0.####} por procesar. Si ya no se fabricará, solicita ajuste ADMIN.",$"result-{stage.Id}",stage.Id));
            if(stage.PendingReceipt>0)result.Add(new($"{stage.Name}: {stage.PendingReceipt:0.####} en entregas pendientes de conciliar.","receive",stage.Id));
            if(stage.AvailableToDeliver>0)result.Add(new($"{stage.Name}: {stage.AvailableToDeliver:0.####} buenas por entregar o recibir en bodega.",stage.Sequence==stages.Max(x=>x.Sequence)?"warehouse":$"deliver-{stage.Id}",stage.Id));
        }
        return result;
    }

    public static bool Allows(ProductionWorkOrderStatus state, string action) => action switch
    {
        "release" or "cancel" => state == ProductionWorkOrderStatus.Draft,
        "pause" or "principal" or "retain" => state is ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress,
        "resume" => state == ProductionWorkOrderStatus.Paused,
        "definitive" => state == ProductionWorkOrderStatus.PrincipalClosed,
        "reopen" => state is ProductionWorkOrderStatus.PrincipalClosed or ProductionWorkOrderStatus.Closed,
        "adjust" => state is ProductionWorkOrderStatus.Draft or ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress or ProductionWorkOrderStatus.Paused,
        "request" or "capture" => state is ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress or ProductionWorkOrderStatus.PrincipalClosed,
        _ => false
    };

    public static IReadOnlyList<ProductionActionOption> OrderActions(ProductionWorkOrderStatus state) =>
        new ProductionActionOption[] { new("release", "Liberar orden", true), new("pause", "Pausar orden", true), new("resume", "Reanudar orden", true), new("cancel", "Cancelar borrador", true) }
            .Where(x => Allows(state, x.Code)).ToArray();

    public static IReadOnlyList<ProductionActionOption> ExecutionActions(ProductionWorkOrderStatus state) =>
        new ProductionActionOption[] { new("principal", "Revisar cierre de fabricación principal", false), new("definitive", "Revisar cierre definitivo", true),
            new("adjust", "Ajustar meta, fecha y materiales", true), new("retain", "Revisar reservas para retrabajo", true),
            new("request", "Solicitar material adicional para retrabajo", true), new("reopen", "Reabrir fabricación", true) }
            .Where(x => Allows(state, x.Code)).ToArray();
}

public sealed record ProductionTaskOption(string Key, string Handler, string Label, Guid? StageId = null,
    Guid? BatchId = null, Guid? DeliveryId = null, Guid? CaseId = null, string? Mode = null, string? Url = null, bool RequiresAdmin = false);

public static class ProductionTaskContext
{
    public static IReadOnlyList<ProductionTaskOption> Build(ProductionOrderDetail order, Guid? batchId,
        IReadOnlyList<ProductionBatchView> batches, IReadOnlyList<ProductionDeliveryView> deliveries, IReadOnlyList<ReworkView> rework, IReadOnlyList<ProductionMaterialIssueRow>? materials=null, bool supplyPending=true)
    {
        var tasks = new List<ProductionTaskOption>();
        if(order.Status == ProductionWorkOrderStatus.Draft) tasks.Add(new("planning", "Planning", "Revisar planificación", RequiresAdmin:true));
        if(ProductionActionPolicy.Allows(order.Status, "capture"))
        {
            if(order.UsesBatchTraceability && batches.Count == 0) tasks.Add(new("batch", "Batch", "Crear lote único"));
            if(!order.UsesBatchTraceability || batchId.HasValue)
            {
                foreach(var delivery in deliveries.Where(x=>x.BatchId==batchId).OrderBy(x=>x.RecordedAt).ThenBy(x=>x.Id))
                    tasks.Add(new($"receive-{delivery.Id}","Handoff",$"Recibir {delivery.Pending:0.####} · {delivery.SourceStage} → {delivery.TargetStage}",delivery.SourceStageId,delivery.BatchId,delivery.Id,Mode:"receive"));
                foreach(var stage in order.Stages.Where(x=>x.AvailableToDeliver>0).OrderBy(x=>x.GoodSince).ThenBy(x=>x.Sequence))
                    tasks.Add(stage.Sequence==order.Stages.Max(x=>x.Sequence)
                        ? new("warehouse","Warehouse","Recibir producto en bodega",stage.Id,batchId)
                        : new($"deliver-{stage.Id}","Handoff",$"Entregar desde {stage.Name}",stage.Id,batchId,Mode:"deliver"));
                foreach(var item in rework.Where(x=>x.BatchId==batchId&&x.Pending>0).OrderBy(x=>x.OriginAt).ThenBy(x=>x.StageId))
                    tasks.Add(new($"rework-{item.Id}","BatchResult",$"Atender retrabajo · {item.Stage}",item.StageId,item.BatchId,CaseId:item.Id));
                foreach(var stage in order.Stages.Where(x=>x.AvailableInput>0 && (order.Status!=ProductionWorkOrderStatus.PrincipalClosed||x.Sequence>1)).OrderBy(x=>x.InputSince).ThenBy(x=>x.Sequence))
                    tasks.Add(new($"result-{stage.Id}",order.UsesBatchTraceability?"BatchResult":"Process",$"Registrar avance · {stage.Name}",stage.Id,batchId));
                if(!order.UsesBatchTraceability)foreach(var stage in order.Stages.Where(x=>x.Rework>0)) tasks.Add(new($"rework-legacy-{stage.Id}","Process",$"Atender retrabajo histórico · {stage.Name}",stage.Id));
                if(!order.UsesBatchTraceability && order.Stages.Any(x=>x.PendingReceipt>0))
                    tasks.Add(new("handoff","Handoff","Recibir o conciliar entrega histórica",Mode:"receive"));
                foreach(var delivery in deliveries.Where(x=>x.BatchId==batchId))
                    tasks.Add(new($"reconcile-{delivery.Id}","Handoff",$"Conciliar entrega {delivery.Id.ToString()[..8]}",delivery.SourceStageId,delivery.BatchId,delivery.Id,Mode:"return",RequiresAdmin:true));
            }
            if(supplyPending)tasks.Add(new("supply","","Consultar surtimientos",Url:$"/Operations/ProductionSupply/Index?Search={Uri.EscapeDataString(order.Number)}"));
            foreach(var stage in order.Stages.Where(s=>materials?.Any(m=>m.StageId==s.Id&&m.Pending>0)==true)) tasks.Add(new($"material-{stage.Id}","Material",$"Devolución y merma · {stage.Name}",stage.Id,batchId));
        }
        foreach(var action in ProductionActionPolicy.OrderActions(order.Status)) tasks.Add(new(action.Code,"Action",action.Label,Mode:action.Code,RequiresAdmin:action.RequiresAdmin));
        if(ProductionActionPolicy.ExecutionActions(order.Status).Count>0)
            tasks.Add(new("execution","","Revisar cierre, reservas y ajustes",Url:$"/Operations/Production/Execution?id={order.Id}"));
        return tasks;
    }
}
