using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed partial class ProductionSupplyServiceTests
{
    [Fact]
    public async Task P5_retained_warehouse_stock_can_be_delivered_after_principal_closure()
    {
        await using var f = await Fixture.CreateAsync(20);
        var orderId = await f.CreateAndReleaseAsync(5);
        var order = await f.Db.ProductionWorkOrders.Include(x => x.Stages).Include(x => x.MaterialPlan).SingleAsync(x => x.Id == orderId);
        var pins = new UserPinService(f.Db, new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="));
        var trace = new ProductionTraceabilityService(f.Db, pins, f.Materials, TimeProvider.System);
        var service = new ProductionExecutionService(f.Db, pins, TimeProvider.System);
        var shift = new ProductionShift { Code = "P5", Name = "Turno P5" };
        f.Db.ProductionShifts.Add(shift); await f.Db.SaveChangesAsync();
        var batch = await trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 5, order.Version, f.AdminPin));
        var result = await trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch.Id!.Value, order.Stages.Single().Id,
            shift.Id, false, 5, 0, 5, 0, [], order.Version, "Revisión sin consumo", f.OperatorPin));
        Assert.True(result.Success, string.Join(";", result.Errors ?? []));
        var rework = Assert.Single(await service.GetReworkAsync(order.Id));
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version,
            "request", f.AdminPin, ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment), "Solicitud que se conciliará",
            CaseId: rework.Id, PlanId: order.MaterialPlan.Single().Id, Quantity: 1))).Status);
        var reservation = await f.Db.ProductionWarehouseReservations.SingleAsync();
        var retained = await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version, "retain", f.AdminPin,
            ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment), "Conservar tres para retrabajo",
            Retentions: [new(rework.Id, null, reservation.Id, 3)]));
        Assert.Equal(ProductionCommandStatus.Success, retained.Status);
        Assert.Equal(3, reservation.Quantity - reservation.ReleasedQuantity);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version,
            "principal", f.OperatorPin, ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Difference), "Retrabajo posterior", f.AdminPin))).Status);
        var line = Assert.Single(await f.Supplies.GetQueueAsync());
        var saved = await f.Preparations.SaveAsync(new(Guid.NewGuid(), line.LineId, line.RequestVersion, f.Wip.Id, null, 0,
            [new(ProductionSupplySourceKind.Warehouse, f.Source.Id, 3)], f.OperatorPin));
        Assert.Equal(ProductionSupplyCommandStatus.Success, saved.Status);
        var view = (await f.Preparations.GetAsync(line.LineId))!;
        var confirmed = await f.Preparations.ConfirmAsync(new(Guid.NewGuid(), view.PreparationId!.Value, view.PreparationVersion, view.Line.RequestVersion, f.OperatorPin));
        Assert.Equal(ProductionSupplyCommandStatus.Success, confirmed.Status);
        Assert.Equal(ProductionWorkOrderStatus.PrincipalClosed, order.Status);
        Assert.Equal(3, Assert.Single(await f.Materials.GetIssuesAsync(order.Id)).Pending);
        Assert.Equal(0, reservation.Quantity - reservation.ReleasedQuantity);
        var plan = Assert.Single(order.MaterialPlan);
        Assert.Equal(ProductionCommandStatus.Success, (await service.ApplyAsync(new(Guid.NewGuid(), order.Id, order.Version,
            "request", f.AdminPin, ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment), "Material adicional",
            CaseId: rework.Id, PlanId: plan.Id, Quantity: 1))).Status);
        Assert.Equal(1, Assert.Single(await f.Supplies.GetQueueAsync()).Pending);
    }

    [Fact]
    public async Task P5_adjustment_invalidates_preparation_and_does_not_release_another_order_stock()
    {
        await using var f = await Fixture.CreateAsync(20);
        var firstId = await f.CreateAndReleaseAsync(5);
        var first = await f.Db.ProductionWorkOrders.Include(x => x.MaterialPlan).SingleAsync(x => x.Id == firstId);
        var secondId = await f.CreateAndReleaseAsync(5);
        var second = await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == secondId);
        var line = (await f.Supplies.GetQueueAsync()).Single(x => x.WorkOrderId == first.Id);
        Assert.Equal(ProductionSupplyCommandStatus.Success, (await f.Preparations.SaveAsync(new(Guid.NewGuid(), line.LineId,
            line.RequestVersion, f.Wip.Id, null, 0, [new(ProductionSupplySourceKind.Warehouse, f.Source.Id, 5)], f.OperatorPin))).Status);
        var view = (await f.Preparations.GetAsync(line.LineId))!;
        var service = new ProductionExecutionService(f.Db, new UserPinService(f.Db, new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")), TimeProvider.System);
        var adjusted = await service.ApplyAsync(new(Guid.NewGuid(), first.Id, first.Version, "adjust", f.AdminPin,
            ProductionModelConfiguration.ReasonId(ProductionReasonCategory.Adjustment), "Reducir pedido", Target: 3, Authorized: 3,
            Materials: [new(first.MaterialPlan.Single().Id, 3)]));
        Assert.Equal(ProductionCommandStatus.Success, adjusted.Status);
        Assert.NotEqual(ProductionSupplyCommandStatus.Success, (await f.Preparations.ConfirmAsync(new(Guid.NewGuid(),
            view.PreparationId!.Value, view.PreparationVersion, view.Line.RequestVersion, f.OperatorPin))).Status);
        var queue = await f.Supplies.GetQueueAsync();
        Assert.Equal(3, queue.Single(x => x.WorkOrderId == first.Id).Reserved);
        Assert.Equal(5, queue.Single(x => x.WorkOrderId == second.Id).Reserved);
    }
}
