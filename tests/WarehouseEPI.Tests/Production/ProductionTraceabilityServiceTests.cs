using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionTraceabilityServiceTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Recipe_is_versioned_and_copied_to_new_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, fixture.Stage.Id, 2)], "Receta inicial", fixture.AdminPin));
        var orderResult = await fixture.Production.CreateOrderAsync(new(Guid.NewGuid(), fixture.Finished.Id,
            25, null, null, null, fixture.AdminPin));

        Assert.True(saved.Success);
        Assert.Equal(ProductionCommandStatus.Success, orderResult.Status);
        var order = await fixture.Db.ProductionWorkOrders.Include(x => x.MaterialPlan).SingleAsync(x => x.Id == orderResult.WorkOrderId);
        Assert.True(order.UsesBatchTraceability);
        Assert.Equal(1, order.RecipeVersion);
        Assert.Equal(5, Assert.Single(order.MaterialPlan).PlannedQuantity);

        await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, fixture.Stage.Id, 3)], "Ajuste", fixture.AdminPin));
        Assert.Equal(5, Assert.Single(order.MaterialPlan).PlannedQuantity);
        Assert.Equal(2, await fixture.Db.ProductionRecipes.CountAsync());
        Assert.Single(await fixture.Db.ProductionRecipes.Where(x => x.IsActive).ToListAsync());
        var operationId = Guid.NewGuid();
        var plan = Assert.Single(order.MaterialPlan);
        var adjusted = await fixture.Trace.AdjustPlanAsync(operationId, plan.Id, 6, order.Version,
            "Cambio autorizado", fixture.AdminPin);
        var retry = await fixture.Trace.AdjustPlanAsync(operationId, plan.Id, 6, order.Version,
            "Cambio autorizado", fixture.AdminPin);
        Assert.True(adjusted.Success);
        Assert.True(retry.Success);
        Assert.Equal(6, plan.PlannedQuantity);
        Assert.Contains(await fixture.Db.ProductionEvents.ToListAsync(), x => x.Type == ProductionEventType.MaterialPlanAdjusted);
    }

    [Fact]
    public async Task Product_configuration_returns_ordered_route_and_active_recipe()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, fixture.Stage.Id, 2)], "Receta inicial", fixture.AdminPin));

        var configuration = await fixture.Trace.GetProductConfigurationAsync(fixture.Finished.Id);

        Assert.NotNull(configuration);
        Assert.True(configuration.ProductIsActive);
        Assert.Equal("Ruta", configuration.RouteName);
        Assert.Equal([1, 2], configuration.Stages.Select(x => x.Sequence));
        Assert.Equal(1, configuration.ActiveRecipe?.Version);
        var line = Assert.Single(configuration.ActiveRecipe!.Lines);
        Assert.Equal(fixture.Material.Id, line.MaterialProductId);
        Assert.Equal(fixture.Stage.Id, line.StageId);
        Assert.True(configuration.ActiveRecipe.IsComplete);
    }

    [Fact]
    public async Task Recipe_without_route_is_saved_as_an_incomplete_version()
    {
        await using var fixture = await Fixture.CreateAsync();
        var route = await fixture.Db.ProductionRoutes.SingleAsync();
        route.IsActive = false;
        await fixture.Db.SaveChangesAsync();

        var saved = await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, null, 2)], "Lista antes de ruta", fixture.AdminPin));

        Assert.True(saved.Success);
        var recipe = await fixture.Db.ProductionRecipes.Include(x => x.Lines).SingleAsync(x => x.IsActive);
        Assert.Null(Assert.Single(recipe.Lines).StageId);
        var configuration = await fixture.Trace.GetProductConfigurationAsync(fixture.Finished.Id);
        Assert.False(configuration!.ActiveRecipe!.IsComplete);
    }

    [Fact]
    public async Task Recipe_rejects_duplicate_unassigned_materials_but_allows_stage_to_remain_pending()
    {
        await using var fixture = await Fixture.CreateAsync();

        var duplicate = await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, null, 1), new(fixture.Material.Id, null, 2)],
            "Lista duplicada", fixture.AdminPin));
        var pending = await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, null, 3)], "Etapa pendiente", fixture.AdminPin));

        Assert.False(duplicate.Success);
        Assert.Contains("repitas", Assert.Single(duplicate.Errors!), StringComparison.OrdinalIgnoreCase);
        Assert.True(pending.Success);
        Assert.Null(Assert.Single((await fixture.Db.ProductionRecipes.Include(x => x.Lines)
            .SingleAsync(x => x.IsActive)).Lines).StageId);
    }

    [Fact]
    public async Task Inactive_finished_product_cannot_receive_a_new_recipe_version()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Finished.IsActive = false;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, fixture.Stage.Id, 2)], "Receta", fixture.AdminPin));

        Assert.False(result.Success);
        Assert.Contains("inactivo", Assert.Single(result.Errors!), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.ProductionRecipes.ToListAsync());
    }

    [Fact]
    public async Task Inactive_material_cannot_be_saved_in_a_recipe()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Material.IsActive = false;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, fixture.Stage.Id, 2)], "Receta", fixture.AdminPin));

        Assert.False(result.Success);
        Assert.Contains("materiales", Assert.Single(result.Errors!), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.ProductionRecipes.ToListAsync());
    }

    [Fact]
    public async Task Batches_are_idempotent_and_cannot_exceed_authorized_quantity()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 1,
            [new(fixture.Material.Id, fixture.Stage.Id, 1)], "Receta", fixture.AdminPin));
        var createdOrder = await fixture.Production.CreateOrderAsync(new(Guid.NewGuid(), fixture.Finished.Id,
            10, null, null, null, fixture.AdminPin));
        var order = await fixture.Db.ProductionWorkOrders.SingleAsync(x => x.Id == createdOrder.WorkOrderId);
        var operationId = Guid.NewGuid();
        var command = new CreateProductionBatchCommand(operationId, order.Id, 6, order.Version, fixture.AdminPin);

        var first = await fixture.Trace.CreateBatchAsync(command);
        var retry = await fixture.Trace.CreateBatchAsync(command);
        var excessive = await fixture.Trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 5, order.Version, fixture.AdminPin));

        Assert.True(first.Success, string.Join("; ", first.Errors ?? []));
        Assert.Equal(first.Id, retry.Id);
        Assert.False(excessive.Success);
        var batch = await fixture.Db.ProductionBatches.Include(x => x.FinishedProductLot).SingleAsync();
        Assert.Equal(batch.Number, batch.FinishedProductLot.Number);
    }

    [Fact]
    public async Task Later_stage_only_processes_quantity_received_for_the_batch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, fixture.Stage.Id, 2)], "Receta", fixture.AdminPin));
        var created = await fixture.Production.CreateOrderAsync(new(Guid.NewGuid(), fixture.Finished.Id,
            10, null, null, null, fixture.AdminPin));
        var order = await fixture.Db.ProductionWorkOrders.Include(x => x.Stages).SingleAsync(x => x.Id == created.WorkOrderId);
        var firstStageId = order.Stages.Single(x => x.Sequence == 1).Id;
        var secondStageId = order.Stages.Single(x => x.Sequence == 2).Id;
        await fixture.Production.ReleaseAsync(new(Guid.NewGuid(), order.Id, order.Version, fixture.AdminPin));
        await fixture.Db.Entry(order).ReloadAsync();
        var batchResult = await fixture.Trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 10, order.Version, fixture.AdminPin));
        await fixture.Db.Entry(order).ReloadAsync();
        var firstOperation = Guid.NewGuid();
        var first = await fixture.Trace.RecordResultAsync(new(firstOperation, order.Id, batchResult.Id!.Value,
            firstStageId, fixture.Shift.Id, false, 10, 10, 0, 0, [], order.Version, "Consumo pendiente", fixture.AdminPin));
        Assert.True(first.Success, string.Join("; ", first.Errors ?? []));
        var delivery = await fixture.Production.DeliverAsync(new(Guid.NewGuid(), order.Id, firstStageId,
            secondStageId, 4, fixture.AdminPin, BatchId: batchResult.Id));
        Assert.Equal(ProductionCommandStatus.Success, delivery.Status);
        var deliveryEvent = await fixture.Db.ProductionEvents.SingleAsync(x => x.BatchId == batchResult.Id && x.Type == ProductionEventType.Delivered);
        var receipt = await fixture.Production.ReceiveAsync(new(Guid.NewGuid(), order.Id, firstStageId,
            secondStageId, 4, fixture.AdminPin, BatchId: batchResult.Id, DeliveryEventId: deliveryEvent.Id));
        Assert.Equal(ProductionCommandStatus.Success, receipt.Status);
        await fixture.Db.Entry(order).ReloadAsync();

        var excessive = await fixture.Trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batchResult.Id.Value,
            secondStageId, fixture.Shift.Id, false, 5, 5, 0, 0, [], order.Version, null, fixture.AdminPin));

        Assert.False(excessive.Success);
        Assert.Contains("excede", Assert.Single(excessive.Errors!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reversing_latest_result_preserves_audited_chain_and_is_idempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Trace.SaveRecipeAsync(new(fixture.Finished.Id, 10,
            [new(fixture.Material.Id, fixture.Stage.Id, 2)], "Receta", fixture.AdminPin));
        var created = await fixture.Production.CreateOrderAsync(new(Guid.NewGuid(), fixture.Finished.Id,
            10, null, null, null, fixture.AdminPin));
        var order = await fixture.Db.ProductionWorkOrders.Include(x => x.Stages).SingleAsync(x => x.Id == created.WorkOrderId);
        var firstStageId = order.Stages.Single(x => x.Sequence == 1).Id;
        await fixture.Production.ReleaseAsync(new(Guid.NewGuid(), order.Id, order.Version, fixture.AdminPin));
        await fixture.Db.Entry(order).ReloadAsync();
        var batch = await fixture.Trace.CreateBatchAsync(new(Guid.NewGuid(), order.Id, 10, order.Version, fixture.AdminPin));
        await fixture.Db.Entry(order).ReloadAsync();
        var result = await fixture.Trace.RecordResultAsync(new(Guid.NewGuid(), order.Id, batch.Id!.Value,
            firstStageId, fixture.Shift.Id, false, 3, 3, 0, 0, [], order.Version, "Sin consumo", fixture.AdminPin));
        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
        await fixture.Db.Entry(order).ReloadAsync();
        var reverseOperation = Guid.NewGuid();

        var reversed = await fixture.Trace.ReverseResultAsync(reverseOperation, result.Id!.Value, order.Version,
            "Captura equivocada", fixture.AdminPin);
        var retry = await fixture.Trace.ReverseResultAsync(reverseOperation, result.Id.Value, order.Version,
            "Captura equivocada", fixture.AdminPin);

        Assert.True(reversed.Success, string.Join("; ", reversed.Errors ?? []));
        Assert.True(retry.Success, string.Join("; ", retry.Errors ?? []));
        Assert.Equal(reversed.Id, retry.Id);
        Assert.Contains(await fixture.Db.ProductionEvents.ToListAsync(), x => x.Type == ProductionEventType.ResultReversed);
        Assert.Equal(0, Assert.Single(await fixture.Trace.GetBatchesAsync(order.Id)).Processed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public WarehouseDbContext Db { get; }
        public ProductionTraceabilityService Trace { get; }
        public ProductionService Production { get; }
        public Product Finished { get; }
        public Product Material { get; }
        public ProductionStage Stage { get; }
        public ProductionStage SecondStage { get; }
        public ProductionShift Shift { get; }
        public readonly string AdminPin = "4826";

        private Fixture(WarehouseDbContext db, ProductionTraceabilityService trace, ProductionService production,
            Product finished, Product material, ProductionStage stage, ProductionStage secondStage, ProductionShift shift) =>
            (Db, Trace, Production, Finished, Material, Stage, SecondStage, Shift) = (db, trace, production, finished, material, stage, secondStage, shift);

        public static async Task<Fixture> CreateAsync()
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await db.Database.EnsureCreatedAsync();
            var pins = new UserPinService(db, new PinProtector(Key));
            var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "", PinHash = "" };
            await pins.AssignAsync(admin, "4826");
            var finished = new Product { Sku = "PT-TRACE", Description = "Terminado", BaseUnitId = 1 };
            var material = new Product { Sku = "MP-TRACE", Description = "Material", BaseUnitId = 1 };
            var stage = new ProductionStage { Code = "TRAZA", Name = "Transformación" };
            var secondStage = new ProductionStage { Code = "EMPAQUE", Name = "Empaque" };
            var wip = new Location { Code = "WIP-TRACE", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
            stage.WipTargets.Add(new ProductionProcessWipTarget { Location = wip });
            var shift = new ProductionShift { Code = "T1", Name = "Turno 1" };
            var route = new ProductionRoute { Product = finished, Name = "Ruta" };
            route.Stages.Add(new ProductionRouteStage { Stage = stage, Sequence = 1 });
            route.Stages.Add(new ProductionRouteStage { Stage = secondStage, Sequence = 2 });
            db.AddRange(admin, material, shift, route, wip);
            await db.SaveChangesAsync();
            var movements = new InventoryMovementService(db, pins, TimeProvider.System);
            var materials = new ProductionMaterialService(db, pins, movements, TimeProvider.System);
            return new(db, new ProductionTraceabilityService(db, pins, materials, TimeProvider.System),
                new ProductionService(db, pins, movements, TimeProvider.System), finished, material, stage, secondStage, shift);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
