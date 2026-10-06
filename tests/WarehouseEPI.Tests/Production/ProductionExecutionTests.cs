using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed partial class ProductionTraceabilityServiceTests
{
    private static ProductionExecutionService Execution(Fixture fixture) => new(fixture.Db,
        new UserPinService(fixture.Db, new PinProtector(Key)), TimeProvider.System);

    private static async Task<ProductionWorkOrder> CreateExecutionOrder(Fixture fixture)
    {
        Assert.True((await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, fixture.Stage.Id, 2)], "Receta", fixture.AdminPin))).Success);
        var result = await fixture.Production.CreateOrderAsync(new(Guid.NewGuid(), fixture.Finished.Id, 10, null, null, null, fixture.AdminPin));
        var order = await fixture.Db.ProductionWorkOrders.Include(x => x.Stages).Include(x => x.MaterialPlan).SingleAsync(x => x.Id == result.WorkOrderId);
        Assert.Equal(ProductionCommandStatus.Success, (await fixture.Production.ReleaseAsync(new(Guid.NewGuid(), order.Id, order.Version, fixture.AdminPin))).Status);
        return order;
    }

    [Fact]
    public async Task P5_adjustment_preserves_original_and_reconciles_requests_idempotently()
    {
        await using var f = await Fixture.CreateAsync();
        var order = await CreateExecutionOrder(f);
        var service = Execution(f);
        var plan = Assert.Single(order.MaterialPlan);
        var command = new ProductionExecutionCommand(Guid.NewGuid(), order.Id, order.Version, "adjust", f.AdminPin,
            ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment), "Menor demanda",
            Target: 8, Authorized: 8, Materials: [new(plan.Id, 1.6m)]);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(command)).Status);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(command)).Status);
        Assert.Equal(10, order.OriginalTargetQuantity);
        Assert.Equal(8, order.TargetQuantity);
        var line = await f.Db.ProductionSupplyRequestLines.SingleAsync();
        Assert.Equal(1.6m, line.RequiredQuantity - line.CancelledQuantity);
        Assert.Single(await f.Db.ProductionExecutionAudits.ToListAsync());
        Assert.Equal(ProductionCommandStatus.IdempotencyConflict, (await service.ApplyAsync(command with { Target = 7 })).Status);
    }

    [Fact]
    public async Task P5_principal_closure_defers_rework_and_operator_cannot_discard_without_admin()
    {
        await using var f = await Fixture.CreateAsync();
        var order = await CreateExecutionOrder(f);
        var pins = new UserPinService(f.Db, new PinProtector(Key));
        var op = new User { FullName = "Operador P5", RoleId = 2, PinHash = "", PinLookup = "" };
        await pins.AssignAsync(op, "7418"); f.Db.Users.Add(op); await f.Db.SaveChangesAsync();
        var batch = await f.Trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 10, order.Version, f.AdminPin));
        var firstStage = order.Stages.Single(x => x.Sequence == 1).Id;
        var initial = await f.Trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch.Id!.Value, firstStage,
            f.Shift.Id, false, 10, 0, 10, 0, [], order.Version, "Revisión", f.AdminPin));
        Assert.True(initial.Success, string.Join(";", initial.Errors ?? []));
        var service = Execution(f);
        var rework = Assert.Single(await service.GetReworkAsync(order.Id));
        Assert.Equal(10, rework.Pending);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version,
            "retain", f.AdminPin, ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment), "Sin material adicional", Retentions: []))).Status);
        var close = new ProductionExecutionCommand(Guid.NewGuid(), order.Id, order.Version, "principal", "7418",
            ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Difference), "Retrabajo diferido", AdminPin: f.AdminPin);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(close)).Status);
        Assert.Equal(ProductionWorkOrderStatus.PrincipalClosed, order.Status);
        var finish = await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version, "definitive", f.AdminPin,
            ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Difference), "Cierre"));
        Assert.Equal(ProductionCommandStatus.ValidationFailed, finish.Status);
        order = await f.Db.ProductionWorkOrders.Include(x => x.Stages).SingleAsync(x => x.Id == order.Id);
        var attempt = new RecordBatchResultCommand(Guid.NewGuid(), order.Id, batch.Id.Value, firstStage, f.Shift.Id,
            true, 10, 0, 8, 2, [], order.Version, "Dos dañadas", "7418", ReworkCaseId: rework.Id);
        Assert.False((await f.Trace.RecordResultAsync(attempt)).Success);
        var recovered = await f.Trace.RecordResultAsync(attempt with { AdminPin = f.AdminPin });
        Assert.True(recovered.Success, string.Join(";", recovered.Errors ?? []));
        var current = Assert.Single(await service.GetReworkAsync(order.Id));
        Assert.Equal(8, current.Pending); Assert.Equal(2, current.Discarded); Assert.Equal(rework.OriginAt, current.OriginAt);
        Assert.Equal(ProductionWorkOrderStatus.PrincipalClosed, (await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == order.Id)).Status);
        Assert.True((await f.Trace.RecordResultAsync(attempt with { AdminPin = f.AdminPin })).Success);
        Assert.Single(await f.Db.ProductionReworkAttempts.ToListAsync());
        var version = (await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == order.Id)).Version;
        Assert.True((await f.Trace.RecordResultAsync(attempt with
        {
            OperationId = Guid.NewGuid(),
            ExpectedOrderVersion = version,
            InputQuantity = 8,
            GoodQuantity = 3,
            ReworkQuantity = 5,
            ScrapQuantity = 0,
            AdminPin = null
        })).Success);
        current = Assert.Single(await service.GetReworkAsync(order.Id));
        Assert.Equal(5, current.Pending); Assert.Equal(3, current.Recovered); Assert.Equal(2, current.Attempts);
        Assert.Equal(rework.OriginAt, current.OriginAt);
    }

    [Fact]
    public async Task P5_operator_closes_without_difference_and_only_admin_finalizes_or_reopens()
    {
        await using var f = await Fixture.CreateAsync(); var order = await CreateExecutionOrder(f);
        var pins = new UserPinService(f.Db, new PinProtector(Key));
        var worker = new User { FullName = "Operador cierre", RoleId = 2, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(worker, "7419");
        var destination = new Location { Code = "PT-P5", Kind = LocationKind.Area };
        f.Db.AddRange(worker, destination); await f.Db.SaveChangesAsync();
        var batch = await f.Trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 10, order.Version, f.AdminPin));
        var stages = order.Stages.OrderBy(x => x.Sequence).ToArray();
        Assert.True((await f.Trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch.Id!.Value, stages[0].Id, f.Shift.Id,
            false, 10, 10, 0, 0, [], order.Version, "Sin consumo", "7419"))).Success);
        Assert.Equal(ProductionCommandStatus.Success, (await f.Production.DeliverAsync(new(Guid.NewGuid(), order.Id,
            stages[0].Id, stages[1].Id, 10, "7419", BatchId: batch.Id))).Status);
        var delivery = await f.Db.ProductionEvents.SingleAsync(x => x.WorkOrderId == order.Id && x.Type == ProductionEventType.Delivered);
        Assert.Equal(ProductionCommandStatus.Success, (await f.Production.ReceiveAsync(new(Guid.NewGuid(), order.Id,
            stages[0].Id, stages[1].Id, 10, "7419", BatchId: batch.Id, DeliveryEventId: delivery.Id))).Status);
        Assert.True((await f.Trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch.Id.Value, stages[1].Id, f.Shift.Id,
            false, 10, 10, 0, 0, [], order.Version, null, "7419"))).Success);
        Assert.Equal(ProductionCommandStatus.Success, (await f.Production.ReceiveWarehouseAsync(new(Guid.NewGuid(), order.Id,
            stages[1].Id, 10, destination.Id, "7419", BatchId: batch.Id))).Status);
        var service = Execution(f);
        var invalidReduction = await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version, "adjust", f.AdminPin,
            ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment), "Reducción inválida", Target: 9, Authorized: 9,
            Materials: [new(order.MaterialPlan.Single().Id, 2)]));
        Assert.Equal(ProductionCommandStatus.ValidationFailed, invalidReduction.Status);
        order = await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal(10, order.TargetQuantity);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version,
            "retain", f.AdminPin, ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment), "Conciliar pendientes", Retentions: []))).Status);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version,
            "principal", "7419", ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Difference), "Sin diferencias"))).Status);
        var finish = new ProductionExecutionCommand(Guid.NewGuid(), order.Id, order.Version, "definitive", "7419",
            ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Difference), "Conciliado");
        Assert.Equal(ProductionCommandStatus.InvalidPin, (await service.ApplyAsync(finish)).Status);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(finish with { Pin = f.AdminPin })).Status);
        Assert.Equal(ProductionWorkOrderStatus.Closed, order.Status);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version,
            "reopen", f.AdminPin, ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment), "Corrección posterior"))).Status);
        Assert.Equal(ProductionWorkOrderStatus.InProgress, order.Status);
        Assert.Equal(1, await f.Db.ProductionExecutionAudits.CountAsync(x => x.WorkOrderId == order.Id && x.Action == "definitive"));
    }

    [Fact]
    public async Task P5_new_lot_uses_whole_order_and_refuses_second_lot()
    {
        await using var f = await Fixture.CreateAsync(); var order = await CreateExecutionOrder(f);
        var first = await f.Trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 3, order.Version, f.AdminPin));
        Assert.True(first.Success);
        Assert.Equal(10, (await f.Db.ProductionBatches.SingleAsync()).AssignedQuantity);
        Assert.False((await f.Trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 1, order.Version, f.AdminPin))).Success);
        Assert.Single(await f.Db.ProductionBatches.ToListAsync());
    }

    [Fact]
    public async Task P5_reason_changes_preserve_snapshots_and_other_requires_comment()
    {
        await using var f = await Fixture.CreateAsync(); var service = Execution(f);
        var id = Guid.NewGuid(); var operation = Guid.NewGuid();
        Assert.Equal(ProductionCommandStatus.Success, (await service.SaveReasonAsync(operation, id, 0,
            ProductionReasonCategory.Scrap, "DAÑO", "Daño físico", true, true, f.AdminPin)).Status);
        Assert.Null(await service.ResolveReasonAsync(id, ProductionReasonCategory.Scrap, null, default));
        var snapshot = await service.ResolveReasonAsync(id, ProductionReasonCategory.Scrap, "Durante corte", default);
        Assert.Contains("Daño físico", snapshot);
        Assert.Equal(ProductionCommandStatus.Success, (await service.SaveReasonAsync(Guid.NewGuid(), id, 1,
            ProductionReasonCategory.Scrap, "DAÑO", "Daño revisado", false, true, f.AdminPin)).Status);
        Assert.Null(await service.ResolveReasonAsync(id, ProductionReasonCategory.Scrap, "Durante corte", default));
        Assert.Contains("Daño físico", snapshot);
        Assert.Equal(2, await f.Db.ProductionExecutionAudits.CountAsync());
    }
}
