using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Production;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class ProductionMaterialPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Existing_order_and_history_survive_batch_traceability_migration()
    {
        await using var db = fixture.CreateDbContext();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var user = new User { FullName = "Admin legado PG", RoleId = 1, PinLookup = $"legacy-{suffix}", PinHash = "legacy" };
        var product = new Product { Sku = $"PG-LEG-{suffix}", Description = "Producto legado", BaseUnitId = 1 };
        var stage = new ProductionStage { Code = $"L-{suffix}", Name = "Proceso legado" };
        var order = new ProductionWorkOrder { CreateOperationId = Guid.NewGuid(), CreateFingerprint = $"legacy-{suffix}",
            Number = $"OT-LEG-{suffix}", Product = product, UnitId = 1, TargetQuantity = 4, AuthorizedQuantity = 4,
            Status = ProductionWorkOrderStatus.InProgress, CreatedByUser = user, UsesBatchTraceability = false };
        var orderStage = new ProductionWorkOrderStage { WorkOrder = order, SourceStage = stage, Sequence = 1,
            Code = stage.Code, Name = stage.Name };
        order.Stages.Add(orderStage);
        order.Events.Add(new ProductionEvent { OperationId = Guid.NewGuid(), RequestFingerprint = $"event-{suffix}",
            WorkOrder = order, Type = ProductionEventType.Created, ResponsibleUser = user, Quantity = 4 });
        db.AddRange(user, stage, order);
        await db.SaveChangesAsync();
        var orderId = order.Id;

        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync("20260910185035_ProductionMaterialOrderLinks");
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var migrated = await db.ProductionWorkOrders.Include(x => x.Events).SingleAsync(x => x.Id == orderId);
        Assert.False(migrated.UsesBatchTraceability);
        Assert.Null(migrated.RecipeVersion);
        Assert.Contains(migrated.Events, x => x.Type == ProductionEventType.Created);
    }

    [Fact]
    public async Task Batch_consumption_partial_receipts_and_trace_are_atomic_on_postgresql()
    {
        await using var db = fixture.CreateDbContext();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var admin = new User { FullName = "Admin traza PG", RoleId = 1, PinLookup = "", PinHash = "" };
        var user = new User { FullName = "Operador traza PG", RoleId = 2, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "2842");
        await pins.AssignAsync(user, "2843");
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var material = new Product { Sku = $"PG-MP-{suffix}", Description = "Materia prima trazable", BaseUnitId = 1 };
        var finished = new Product { Sku = $"PG-PT-{suffix}", Description = "Terminado trazable", BaseUnitId = 1 };
        var source = new Location { Code = "Z-91-1", Kind = LocationKind.Rack, RowCode = "Z", RackNumber = 91, PalletNumber = 1 };
        var wip = new Location { Code = $"W-{suffix}", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var destination = new Location { Code = $"D-{suffix}", Kind = LocationKind.Area };
        var stage = new ProductionStage { Code = $"P-{suffix}", Name = "Transformación PG" };
        stage.WipTargets.Add(new ProductionProcessWipTarget { Location = wip });
        var shift = new ProductionShift { Code = $"T-{suffix}", Name = "Turno traza" };
        var route = new ProductionRoute { Product = finished, Name = "Ruta trazable" };
        route.Stages.Add(new ProductionRouteStage { Stage = stage, Sequence = 1 });
        db.AddRange(admin, user, material, source, destination, shift, route);
        await db.SaveChangesAsync();

        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        var materialService = new ProductionMaterialService(db, pins, movements, TimeProvider.System);
        var trace = new ProductionTraceabilityService(db, pins, materialService, TimeProvider.System);
        var production = new ProductionService(db, pins, movements, TimeProvider.System);
        Assert.True((await trace.SaveRecipeAsync(new(finished.Id, 5,
            [new(material.Id, stage.Id, 5)], "Receta PG", "2842"))).Success);
        var created = await production.CreateOrderAsync(new(Guid.NewGuid(), finished.Id, 10, null, null, null, "2842"));
        var order = await db.ProductionWorkOrders.Include(x => x.Stages).SingleAsync(x => x.Id == created.WorkOrderId);
        Assert.Equal(ProductionCommandStatus.Success, (await production.ReleaseAsync(new(Guid.NewGuid(), order.Id, order.Version, "2842"))).Status);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(InventoryMovementStatus.Success, (await movements.ConfirmAsync(new InventoryMovementCommand(Guid.NewGuid(),
            InventoryMovementType.Entry, "2843", [new InventoryMovementLineCommand(material.Id, 5, DestinationLocationId: source.Id)]))).Status);
        var stageId = Assert.Single(order.Stages).Id;
        Assert.Equal(InventoryMovementStatus.Success, (await materialService.IssueAsync(new InventoryMovementCommand(Guid.NewGuid(),
            InventoryMovementType.Transfer, "2843", [new InventoryMovementLineCommand(material.Id, 5, SourceLocationId: source.Id, DestinationLocationId: wip.Id)],
            Purpose: InventoryMovementPurpose.ProductionIssue, OperationalAreaId: wip.Id), order.Id, stageId, order.Version)).Status);
        await db.Entry(order).ReloadAsync();
        var batch = await trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 5, order.Version, "2842"));
        await db.Entry(order).ReloadAsync();
        var issue = await db.ProductionMaterialIssueLinks.SingleAsync(x => x.WorkOrderId == order.Id);
        var result = await trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch.Id!.Value, stageId, shift.Id,
            false, 5, 5, 0, 0, [new(issue.Id, 5)], order.Version, null, "2843"));
        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));

        await db.Entry(order).ReloadAsync();
        var rawLot2 = new ProductLot { ProductId = material.Id, Number = $"RAW-{suffix}-B",
            NormalizedNumber = $"RAW-{suffix}-B", CreatedAt = DateTimeOffset.UtcNow };
        db.ProductLots.Add(rawLot2);
        await db.SaveChangesAsync();
        Assert.Equal(InventoryMovementStatus.Success, (await movements.ConfirmAsync(new InventoryMovementCommand(Guid.NewGuid(),
            InventoryMovementType.Entry, "2843", [new InventoryMovementLineCommand(material.Id, 5,
                DestinationLocationId: source.Id, DestinationLotId: rawLot2.Id)]))).Status);
        Assert.Equal(InventoryMovementStatus.Success, (await materialService.IssueAsync(new InventoryMovementCommand(Guid.NewGuid(),
            InventoryMovementType.Transfer, "2843", [new InventoryMovementLineCommand(material.Id, 5, SourceLocationId: source.Id, DestinationLocationId: wip.Id)],
            Purpose: InventoryMovementPurpose.ProductionIssue, OperationalAreaId: wip.Id), order.Id, stageId, order.Version)).Status);
        await db.Entry(order).ReloadAsync();
        var batch2 = await trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 5, order.Version, "2842"));
        await db.Entry(order).ReloadAsync();
        var issue2 = await db.ProductionMaterialIssueLinks.Where(x => x.WorkOrderId == order.Id &&
            x.InventoryMovementLine.BalanceChanges.Any(c => c.LotId == rawLot2.Id && c.DeltaQuantity > 0)).SingleAsync();
        var result2 = await trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch2.Id!.Value, stageId, shift.Id,
            false, 5, 5, 0, 0, [new(issue2.Id, 5)], order.Version, null, "2843"));
        Assert.True(result2.Success, string.Join("; ", result2.Errors ?? []));

        Assert.Equal(ProductionCommandStatus.Success, (await production.ReceiveWarehouseAsync(new(Guid.NewGuid(), order.Id,
            stageId, 2, destination.Id, "2843", BatchId: batch.Id))).Status);
        Assert.Equal(ProductionCommandStatus.Success, (await production.ReceiveWarehouseAsync(new(Guid.NewGuid(), order.Id,
            stageId, 3, destination.Id, "2843", BatchId: batch.Id))).Status);

        var links = await trace.GetTraceAsync(batch.Id.Value);
        var links2 = await trace.GetTraceAsync(batch2.Id.Value);
        var batchEntity = await db.ProductionBatches.SingleAsync(x => x.Id == batch.Id);
        var receiptLots = await db.InventoryBalanceChanges.Where(x => x.LocationId == destination.Id && x.DeltaQuantity > 0)
            .Select(x => x.LotId).Distinct().ToListAsync();
        Assert.Equal(5, Assert.Single(links).Quantity);
        Assert.Equal(5, Assert.Single(links2).Quantity);
        Assert.NotEqual(links[0].MaterialLot, links2[0].MaterialLot);
        Assert.Equal((await db.ProductionBatches.SingleAsync(x => x.Id == batch.Id)).Number,
            Assert.Single(await trace.SearchTraceAsync(links[0].MaterialLot)).BatchNumber);
        Assert.Equal(batch2.Id, (await db.ProductionBatches.SingleAsync(x => x.Number == links2[0].BatchNumber)).Id);
        Assert.Equal(batchEntity.FinishedProductLotId, Assert.Single(receiptLots));
        Assert.NotEmpty(await trace.SearchTraceAsync(links[0].MaterialLot));
        Assert.NotEmpty(await trace.SearchTraceAsync(links[0].FinishedLot));
        var page = await new ProductionQueryService(db).SearchOrdersAsync(new(batchEntity.Number, null, null, null, null, false));
        Assert.Equal(order.Id, Assert.Single(page.Items).Id);
        await db.Entry(order).ReloadAsync();
        var reversed = await trace.ReverseResultAsync(Guid.NewGuid(), result2.Id!.Value, order.Version,
            "Validación de reverso PG", "2842");
        Assert.True(reversed.Success, string.Join("; ", reversed.Errors ?? []));
        Assert.Empty(await trace.GetTraceAsync(batch2.Id.Value));
        Assert.Equal(5, (await materialService.GetIssuesAsync(order.Id)).Single(x => x.IssueLinkId == issue2.Id).Pending);
    }

    [Fact]
    public async Task Linked_issue_consumption_and_reserved_lots_are_atomic_on_postgresql()
    {
        await using var db = fixture.CreateDbContext();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var admin = new User { FullName = "Admin material PG", RoleId = 1, PinLookup = "", PinHash = "" };
        var user = new User { FullName = "Operador material PG", RoleId = 2, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "1842");
        await pins.AssignAsync(user, "1843");
        var material = new Product { Sku = "PG-MAT-01", Description = "Material PG", BaseUnitId = 1 };
        var finished = new Product { Sku = "PG-PT-01", Description = "Terminado PG", BaseUnitId = 1 };
        var source = new Location { Code = "P-1-1", Kind = LocationKind.Rack, RowCode = "P", RackNumber = 1, PalletNumber = 1 };
        var wip = new Location { Code = "PG-WIP-MAT", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var process = new ProductionStage { Code = "PG-COS", Name = "Costura PG" };
        process.WipTargets.Add(new ProductionProcessWipTarget { Location = wip });
        var order = new ProductionWorkOrder
        {
            CreateOperationId = Guid.NewGuid(), CreateFingerprint = "pg-material", Number = "PG-OT-0001",
            Product = finished, UnitId = 1, TargetQuantity = 10, AuthorizedQuantity = 10,
            Status = ProductionWorkOrderStatus.Released, CreatedByUser = admin
        };
        var stage = new ProductionWorkOrderStage
        {
            WorkOrder = order, SourceStage = process, Sequence = 1, Code = process.Code, Name = process.Name
        };
        order.Stages.Add(stage);
        db.AddRange(admin, user, material, source, wip, process, order);
        await db.SaveChangesAsync();

        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        Assert.Equal(InventoryMovementStatus.Success, (await movements.ConfirmAsync(new InventoryMovementCommand(
            Guid.NewGuid(), InventoryMovementType.Entry, "1843",
            [new InventoryMovementLineCommand(material.Id, 8, DestinationLocationId: source.Id)]))).Status);
        var service = new ProductionMaterialService(db, pins, movements, TimeProvider.System);
        var issue = await service.IssueAsync(new InventoryMovementCommand(Guid.NewGuid(), InventoryMovementType.Transfer,
            "1843", [new InventoryMovementLineCommand(material.Id, 6, SourceLocationId: source.Id, DestinationLocationId: wip.Id)],
            Purpose: InventoryMovementPurpose.ProductionIssue, OperationalAreaId: wip.Id), order.Id, stage.Id, order.Version);
        Assert.Equal(InventoryMovementStatus.Success, issue.Status);
        var link = await db.ProductionMaterialIssueLinks.SingleAsync();

        var consumption = await service.ApplyAsync(new ProductionMaterialCommand(Guid.NewGuid(), order.Id, stage.Id,
            order.Version, ProductionMaterialOperationType.Consumption,
            [new ProductionMaterialSelection(link.Id, 2)], "1843"));
        var general = await movements.ConfirmAsync(new InventoryMovementCommand(Guid.NewGuid(), InventoryMovementType.Exit,
            "1843", [new InventoryMovementLineCommand(material.Id, 1, SourceLocationId: wip.Id)],
            Purpose: InventoryMovementPurpose.WipConsumption, OperationalAreaId: wip.Id));

        Assert.Equal(ProductionMaterialStatus.Success, consumption.Status);
        Assert.Equal(InventoryMovementStatus.ValidationFailed, general.Status);
        Assert.Equal(4, Assert.Single(await service.GetIssuesAsync(order.Id)).Pending);
        Assert.Equal(4, await db.InventoryBalances.Where(x => x.ProductId == material.Id && x.LocationId == wip.Id)
            .SumAsync(x => x.Quantity));
    }
}
