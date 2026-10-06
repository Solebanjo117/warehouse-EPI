using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Production;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class ProductionBlockBPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Queues_page_whole_orders_in_same_priority_order_and_detect_content_changes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"B-{suffix}", $"B-{suffix}", "5843");
        await using var db = fixture.CreateDbContext();
        var user = await db.Users.SingleAsync(x => x.FullName == $"Operador B-{suffix}");
        var stage = new ProductionStage { Code = $"B-{suffix}", Name = "Proceso B", InactivityAlertHours = 1 };
        var orders = new List<ProductionWorkOrder>();
        for (var i = 0; i < 27; i++)
        {
            var order = new ProductionWorkOrder
            {
                Number = $"B-{suffix}-{i:00}",
                CreateOperationId = Guid.NewGuid(),
                CreateFingerprint = $"b-{i}",
                ProductId = seed.ProductId,
                UnitId = 1,
                CreatedByUserId = user.Id,
                TargetQuantity = 10,
                AuthorizedQuantity = 10,
                OriginalTargetQuantity = 10,
                Status = ProductionWorkOrderStatus.Released,
                SupplyPriority = i % 3 == 0 ? ProductionSupplyPriority.Urgent : ProductionSupplyPriority.Normal,
                DueDate = i % 2 == 0 ? new DateOnly(2026, 9, 20) : null,
                CreatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i)
            };
            var step = new ProductionWorkOrderStage { SourceStage = stage, Sequence = 1, Code = stage.Code, Name = stage.Name }; order.Stages.Add(step);
            var plan = new ProductionOrderMaterialPlan { WorkOrderStage = step, MaterialProductId = seed.ProductId, UnitId = 1, PlannedQuantity = 20, OriginalPlannedQuantity = 20 }; order.MaterialPlan.Add(plan);
            var request = new ProductionSupplyRequest { WorkOrderStage = step }; order.SupplyRequests.Add(request);
            request.Lines.Add(new ProductionSupplyRequestLine { MaterialPlan = plan, ProductId = seed.ProductId, UnitId = 1, RequiredQuantity = 20 });
            orders.Add(order);
        }
        db.AddRange(orders); await db.SaveChangesAsync();
        var supplies = new ProductionSupplyService(db, new UserPinService(db, new PinProtector(Convert.ToBase64String(new byte[32]))), TimeProvider.System);
        var first = await supplies.GetQueuePageAsync(suffix); var second = await supplies.GetQueuePageAsync(suffix, page: 2);
        Assert.Equal(27, first.TotalOrders); Assert.Equal(25, first.Rows.Select(x => x.WorkOrderId).Distinct().Count()); Assert.Equal(2, second.Rows.Count);
        var expected = orders.OrderByDescending(x => x.SupplyPriority).ThenBy(x => x.DueDate == null).ThenBy(x => x.DueDate).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id).ToArray();
        Assert.Equal(expected, first.Rows.Concat(second.Rows).Select(x => x.WorkOrderId));
        var query = new ProductionQueryService(db);
        var production = await query.SearchOrdersAsync(new(Search: suffix, PageSize: 100, View: "active"));
        Assert.Equal(expected, production.Items.Select(x => x.Id));
        var alerted = await query.SearchOrdersAsync(new(Search: suffix, AlertsOnly: true, PageSize: 100));
        Assert.Equal(27, alerted.TotalCount); Assert.All(alerted.Items, x => Assert.True(x.AlertCount > 0));
        var at = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        var attention = await ProductionAttentionQuery.Query(db, db.ProductionWorkOrders.Where(x => x.Id == orders[0].Id), at, new DateOnly(2026, 9, 16)).SingleAsync();
        Assert.Equal(1, attention.Inactive); Assert.Equal(1, attention.PendingLines);
        orders[0].Status = ProductionWorkOrderStatus.Paused; await db.SaveChangesAsync();
        Assert.Equal(0, (await ProductionAttentionQuery.Query(db, db.ProductionWorkOrders.Where(x => x.Id == orders[0].Id), at, new DateOnly(2026, 9, 16)).SingleAsync()).Inactive);
        orders[0].Status = ProductionWorkOrderStatus.Released; stage.InactivityAlertHours = null; await db.SaveChangesAsync();
        Assert.Equal(0, (await ProductionAttentionQuery.Query(db, db.ProductionWorkOrders.Where(x => x.Id == orders[0].Id), at, new DateOnly(2026, 9, 16)).SingleAsync()).Inactive);
        var before = await query.GetSnapshotAsync(new(Search: suffix));
        orders[0].SupplyRequests.Single().Lines.Single().RequiredQuantity = 19; await db.SaveChangesAsync();
        var changed = await supplies.GetQueuePageAsync(suffix);
        Assert.Equal(first.TotalOrders, changed.TotalOrders); Assert.NotEqual(first.Signature, changed.Signature);
        Assert.NotEqual(before.Signature, (await query.GetSnapshotAsync(new(Search: suffix))).Signature);
        Assert.Equal(2, (await supplies.GetQueuePageAsync(suffix, page: 99)).Page);
        Assert.Equal(27, (await query.SearchOrdersAsync(new(Search: suffix, ActiveStageId: stage.Id, PageSize: 100))).TotalCount);
        foreach (var order in orders)
        {
            db.ProductionEvents.Add(new ProductionEvent { WorkOrder = order, OperationId = Guid.NewGuid(), RequestFingerprint = "processed", Type = ProductionEventType.Processed, WorkOrderStage = order.Stages.Single(), ResponsibleUserId = user.Id, Quantity = 10, GoodQuantity = 0, ScrapQuantity = 10 });
        }
        await db.SaveChangesAsync();
        Assert.Equal(0, (await query.SearchOrdersAsync(new(Search: suffix, ActiveStageId: stage.Id))).TotalCount);
        var downstream = new ProductionStage { Code = $"BD-{suffix}", Name = "Proceso terminado" };
        var step2 = new ProductionWorkOrderStage { WorkOrder = orders[0], SourceStage = downstream, Code = downstream.Code, Name = downstream.Name, Sequence = 2 }; db.ProductionWorkOrderStages.Add(step2);
        db.ProductionEvents.Add(new ProductionEvent { WorkOrder = orders[0], WorkOrderStage = orders[0].Stages.First(x => x.Sequence == 1), RelatedStage = step2, Type = ProductionEventType.Received, Quantity = 10, OperationId = Guid.NewGuid(), RequestFingerprint = "old-received", ResponsibleUserId = user.Id });
        var processed = new ProductionEvent { WorkOrder = orders[0], WorkOrderStage = step2, Type = ProductionEventType.Processed, Quantity = 10, ScrapQuantity = 10, OperationId = Guid.NewGuid(), RequestFingerprint = "done", ResponsibleUserId = user.Id }; db.ProductionEvents.Add(processed); await db.SaveChangesAsync();
        Assert.Equal(0, (await query.SearchOrdersAsync(new(Search: suffix, ActiveStageId: downstream.Id))).TotalCount);
        db.ProductionEvents.Add(new ProductionEvent { WorkOrder = orders[0], RelatedEvent = processed, Type = ProductionEventType.ResultReversed, OperationId = Guid.NewGuid(), RequestFingerprint = "undo", ResponsibleUserId = user.Id }); await db.SaveChangesAsync();
        Assert.Equal(1, (await query.SearchOrdersAsync(new(Search: suffix, ActiveStageId: downstream.Id))).TotalCount);
    }
}
