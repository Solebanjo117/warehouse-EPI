using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed partial class ProductionTraceabilityServiceTests
{
    [Fact]
    public async Task B_tasks_preserve_states_and_recommend_identified_receipt_before_more_work()
    {
        await using var f = await Fixture.CreateAsync();
        var order = await CreateExecutionOrder(f);
        var batch = await f.Trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 10, order.Version, f.AdminPin));
        var stages = order.Stages.OrderBy(x => x.Sequence).ToArray();
        Assert.True((await f.Trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch.Id!.Value, stages[0].Id, f.Shift.Id, false, 3, 3, 0, 0, [], order.Version, "Sin consumo", f.AdminPin))).Success);
        Assert.Equal(ProductionCommandStatus.Success, (await f.Production.DeliverAsync(new(Guid.NewGuid(), order.Id, stages[0].Id, stages[1].Id, 2, f.AdminPin, BatchId: batch.Id, ExpectedVersion: order.Version))).Status);
        var query = new ProductionQueryService(f.Db); var detail = (await query.GetAsync(order.Id))!;
        var deliveries = await f.Trace.GetPendingDeliveriesAsync(order.Id);
        var batches = await f.Trace.GetBatchesAsync(order.Id);
        var tasks = ProductionTaskContext.Build(detail, batch.Id, batches, deliveries, []);
        Assert.Equal("receive", tasks[0].Mode); Assert.Equal(Assert.Single(deliveries).Id, tasks[0].DeliveryId);
        Assert.Equal("Transformación", deliveries[0].SourceStage); Assert.Equal("Empaque", deliveries[0].TargetStage); Assert.Equal("Admin", deliveries[0].Responsible);
        Assert.Contains(tasks, x => x.Mode == "deliver"); Assert.Contains(tasks, x => x.Handler == "BatchResult");
        var paused = ProductionTaskContext.Build(detail with { Status = ProductionWorkOrderStatus.Paused }, batch.Id, batches, deliveries, []);
        Assert.DoesNotContain(paused, x => x.Handler == "BatchResult" || x.Handler == "Handoff"); Assert.Contains(paused, x => x.Mode == "resume" && x.RequiresAdmin);
        var cancelled = ProductionTaskContext.Build(detail with { Status = ProductionWorkOrderStatus.Cancelled }, batch.Id, batches, deliveries, []); Assert.Empty(cancelled);
        Assert.Equal("reopen", Assert.Single(ProductionActionPolicy.ExecutionActions(ProductionWorkOrderStatus.Closed)).Code);
        Assert.Equal(ProductionCommandStatus.Success, (await f.Production.ReturnDifferenceAsync(new(Guid.NewGuid(), order.Id, stages[0].Id, stages[1].Id, 1, f.AdminPin, "Retorno", batch.Id, deliveries[0].Id, order.Version))).Status);
        Assert.Equal(ProductionCommandStatus.Success, (await f.Production.DeliverAsync(new(Guid.NewGuid(), order.Id, stages[0].Id, stages[1].Id, 2, f.AdminPin, BatchId: batch.Id, ExpectedVersion: order.Version))).Status);
        var separate = await f.Trace.GetPendingDeliveriesAsync(order.Id); Assert.Equal(2, separate.Count); Assert.Equal(2, separate.Select(x => x.Id).Distinct().Count()); Assert.All(separate, x => Assert.Equal(2, x.Delivered));
        var all = ProductionTaskContext.Build(detail, null, batches, deliveries, []); Assert.DoesNotContain(all, x => x.Handler == "BatchResult" || x.Handler == "Warehouse");
    }

    [Fact]
    public async Task B_paused_execution_rejects_manipulated_capture_action()
    {
        await using var f = await Fixture.CreateAsync(); var order = await CreateExecutionOrder(f);
        Assert.Equal(ProductionCommandStatus.Success, (await f.Production.PauseAsync(new(Guid.NewGuid(), order.Id, order.Version, f.AdminPin, "Revisión"))).Status);
        var result = await Execution(f).ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version, "principal", f.AdminPin, ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Difference), "Manipulado"));
        Assert.Equal(ProductionCommandStatus.ValidationFailed, result.Status); Assert.Equal(ProductionWorkOrderStatus.Paused, (await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == order.Id)).Status);
    }
}
