using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionPlanningServiceTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Material_default_has_precedence_and_order_keeps_its_snapshot_after_catalog_change()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.ProductionMaterialWipDefaults.Add(new() { ProductId = f.Material.Id, ProductionStageId = f.Stage.Id, LocationId = f.WipA.Id });
        f.Stage.DefaultWipLocationId = f.WipB.Id;
        await f.Db.SaveChangesAsync();

        var orderId = await f.CreateOrderAsync();
        var plan = await f.Db.ProductionOrderMaterialPlans.SingleAsync(x => x.WorkOrderId == orderId);
        Assert.Equal(f.WipA.Id, plan.WipLocationId);
        Assert.Equal(ProductionWipResolutionSource.MaterialDefault, plan.WipResolutionSource);

        var rule = await f.Db.ProductionMaterialWipDefaults.SingleAsync();
        rule.LocationId = f.WipB.Id;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        plan = await f.Db.ProductionOrderMaterialPlans.SingleAsync(x => x.WorkOrderId == orderId);
        Assert.Equal(f.WipA.Id, plan.WipLocationId);
        Assert.Equal("WIP-A", plan.WipTargetCode);
    }

    [Fact]
    public async Task Process_default_then_single_concrete_target_are_used_without_accepting_a_row_as_destination()
    {
        await using var f = await Fixture.CreateAsync();
        f.Stage.DefaultWipLocationId = f.WipB.Id;
        await f.Db.SaveChangesAsync();
        var processOrder = await f.CreateOrderAsync();
        Assert.Equal(ProductionWipResolutionSource.ProcessDefault,
            (await f.Db.ProductionOrderMaterialPlans.SingleAsync(x => x.WorkOrderId == processOrder)).WipResolutionSource);

        f.Stage.DefaultWipLocationId = null;
        f.Db.ProductionProcessWipTargets.RemoveRange(await f.Db.ProductionProcessWipTargets.ToListAsync());
        f.Db.ProductionProcessWipTargets.Add(new() { ProductionStageId = f.Stage.Id, LocationId = f.WipA.Id });
        f.Db.ProductionProcessWipTargets.Add(new() { ProductionStageId = f.Stage.Id, RowCode = "Z" });
        await f.Db.SaveChangesAsync();
        var uniqueOrder = await f.CreateOrderAsync();
        var uniquePlan = await f.Db.ProductionOrderMaterialPlans.SingleAsync(x => x.WorkOrderId == uniqueOrder);
        Assert.Equal(f.WipA.Id, uniquePlan.WipLocationId);
        Assert.Equal(ProductionWipResolutionSource.SingleProcessTarget, uniquePlan.WipResolutionSource);
    }

    [Fact]
    public async Task Manual_override_is_audited_idempotent_and_does_not_change_P1_defaults()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.ProductionMaterialWipDefaults.Add(new() { ProductId = f.Material.Id, ProductionStageId = f.Stage.Id, LocationId = f.WipA.Id });
        await f.Db.SaveChangesAsync();
        var orderId = await f.CreateOrderAsync();
        var order = await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == orderId);
        var plan = await f.Db.ProductionOrderMaterialPlans.SingleAsync(x => x.WorkOrderId == orderId);
        var operation = Guid.NewGuid();
        var command = new ReviewProductionPlanningCommand(operation, orderId, order.Version,
            [new(plan.Id, $"P:{f.WipB.Id}")], "Excepción para esta orden", f.AdminPin);

        var first = await f.Planning.ReviewAsync(command);
        var retry = await f.Planning.ReviewAsync(command);
        var conflict = await f.Planning.ReviewAsync(command with { Targets = [new(plan.Id, $"P:{f.WipA.Id}")] });

        Assert.Equal(ProductionPlanningStatus.Success, first.Status);
        Assert.Equal(ProductionPlanningStatus.Success, retry.Status);
        Assert.Equal(ProductionPlanningStatus.IdempotencyConflict, conflict.Status);
        Assert.Equal(ProductionWipResolutionSource.Manual, (await f.Db.ProductionOrderMaterialPlans.SingleAsync(x => x.Id == plan.Id)).WipResolutionSource);
        Assert.Equal(f.WipA.Id, (await f.Db.ProductionMaterialWipDefaults.SingleAsync()).LocationId);
        var revision = await f.Db.ProductionOrderPlanningRevisions.SingleAsync();
        Assert.Contains("P:", revision.BeforeJson);
        Assert.Contains("Excepción", revision.Reason);
    }

    [Fact]
    public async Task Release_blocks_unresolved_plan_but_inventory_shortage_is_only_a_warning()
    {
        await using var f = await Fixture.CreateAsync();
        var orderId = await f.CreateOrderAsync();
        var order = await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == orderId);
        var blocked = await f.Production.ReleaseAsync(new(Guid.NewGuid(), orderId, order.Version, f.AdminPin));
        Assert.Equal(ProductionCommandStatus.ValidationFailed, blocked.Status);
        Assert.Contains(blocked.ValidationErrors, x => x.Contains("destino WIP", StringComparison.OrdinalIgnoreCase));

        var plan = await f.Db.ProductionOrderMaterialPlans.SingleAsync(x => x.WorkOrderId == orderId);
        var reviewed = await f.Planning.ReviewAsync(new(Guid.NewGuid(), orderId, order.Version,
            [new(plan.Id, $"P:{f.WipA.Id}")], "Completar destino", f.AdminPin));
        Assert.True(reviewed.Status == ProductionPlanningStatus.Success, $"{reviewed.Status}: {string.Join(" | ", reviewed.Errors ?? [])}");
        var view = await f.Planning.GetAsync(orderId);
        Assert.True(view.CanRelease);
        Assert.Contains(view.Warnings, x => x.Contains("no bloquea", StringComparison.OrdinalIgnoreCase));

        order = await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == orderId);
        var released = await f.Production.ReleaseAsync(new(Guid.NewGuid(), orderId, order.Version, f.AdminPin));
        Assert.Equal(ProductionCommandStatus.Success, released.Status);
    }

    [Fact]
    public async Task Invalid_configured_target_is_preserved_in_P1_but_not_copied_to_a_new_order()
    {
        await using var f = await Fixture.CreateAsync();
        f.WipA.IsBlocked = true;
        f.Db.ProductionMaterialWipDefaults.Add(new() { ProductId = f.Material.Id, ProductionStageId = f.Stage.Id, LocationId = f.WipA.Id });
        f.Stage.DefaultWipLocationId = f.WipB.Id;
        await f.Db.SaveChangesAsync();

        var orderId = await f.CreateOrderAsync();
        var plan = await f.Db.ProductionOrderMaterialPlans.SingleAsync(x => x.WorkOrderId == orderId);
        Assert.Null(plan.WipLocationId);
        Assert.Equal(f.WipA.Id, (await f.Db.ProductionMaterialWipDefaults.SingleAsync()).LocationId);
    }

    [Fact]
    public async Task Legacy_draft_can_take_the_first_recipe_once_and_cannot_be_reviewed_after_release()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.ProductionRecipes.RemoveRange(await f.Db.ProductionRecipes.Include(x => x.Lines).ToListAsync());
        f.Stage.DefaultWipLocationId = f.WipA.Id;
        await f.Db.SaveChangesAsync();
        var orderId = await f.CreateOrderAsync();
        Assert.Empty(await f.Db.ProductionOrderMaterialPlans.Where(x => x.WorkOrderId == orderId).ToListAsync());
        var order = await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == orderId);
        var admin = await f.Db.Users.SingleAsync(x => x.RoleId == 1);
        f.Db.ProductionRecipes.Add(new() { ProductId = f.Finished.Id, Version = 2, BaseQuantity = 10,
            Reason = "Primera receta disponible", CreatedByUserId = admin.Id,
            Lines = { new ProductionRecipeLine { MaterialProductId = f.Material.Id, StageId = f.Stage.Id, Quantity = 5 } } });
        await f.Db.SaveChangesAsync();

        var invalidPin = await f.Planning.ReviewAsync(new(Guid.NewGuid(), orderId, order.Version, [], "Revisar", "0000"));
        var stale = await f.Planning.ReviewAsync(new(Guid.NewGuid(), orderId, order.Version + 1, [], "Revisar", f.AdminPin));
        var reviewed = await f.Planning.ReviewAsync(new(Guid.NewGuid(), orderId, order.Version, [], "Incorporar receta", f.AdminPin));
        Assert.Equal(ProductionPlanningStatus.InvalidPin, invalidPin.Status);
        Assert.Equal(ProductionPlanningStatus.ConcurrencyConflict, stale.Status);
        Assert.True(reviewed.Status == ProductionPlanningStatus.Success, $"{reviewed.Status}: {string.Join(" | ", reviewed.Errors ?? [])}");
        var plan = await f.Db.ProductionOrderMaterialPlans.SingleAsync(x => x.WorkOrderId == orderId);
        Assert.Equal(ProductionWipResolutionSource.ProcessDefault, plan.WipResolutionSource);
        Assert.Equal(2, (await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == orderId)).RecipeVersion);
        var revision = await f.Db.ProductionOrderPlanningRevisions.SingleAsync();
        Assert.Contains("RecipeVersion", revision.BeforeJson);
        Assert.Contains(plan.Id.ToString(), revision.AfterJson);

        order = await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == orderId);
        Assert.Equal(ProductionCommandStatus.Success, (await f.Production.ReleaseAsync(new(Guid.NewGuid(), orderId, order.Version, f.AdminPin))).Status);
        order = await f.Db.ProductionWorkOrders.SingleAsync(x => x.Id == orderId);
        var afterRelease = await f.Planning.ReviewAsync(new(Guid.NewGuid(), orderId, order.Version,
            [new(plan.Id, $"P:{f.WipB.Id}")], "Cambio tardío", f.AdminPin));
        Assert.Equal(ProductionPlanningStatus.ValidationFailed, afterRelease.Status);
    }

    [Fact]
    public async Task Order_without_route_stays_draft_and_review_adds_first_complete_snapshot()
    {
        await using var f = await Fixture.CreateAsync();
        var route = await f.Db.ProductionRoutes.Include(x => x.Stages).SingleAsync();
        var recipe = await f.Db.ProductionRecipes.Include(x => x.Lines).SingleAsync();
        route.IsActive = false;
        recipe.IsActive = false;
        var admin = await f.Db.Users.SingleAsync(x => x.RoleId == 1);
        f.Db.ProductionRecipes.Add(new ProductionRecipe
        {
            ProductId = f.Finished.Id, Version = 2, BaseQuantity = 10, IsActive = true,
            Reason = "Lista incompleta", CreatedByUserId = admin.Id,
            Lines = { new ProductionRecipeLine { MaterialProductId = f.Material.Id, Quantity = 5 } }
        });
        await f.Db.SaveChangesAsync();

        var orderId = await f.CreateOrderAsync();
        var order = await f.Db.ProductionWorkOrders.Include(x => x.Stages).Include(x => x.MaterialPlan)
            .SingleAsync(x => x.Id == orderId);
        Assert.Empty(order.Stages);
        Assert.Null(order.RecipeVersion);
        Assert.Equal(ProductionCommandStatus.ValidationFailed,
            (await f.Production.ReleaseAsync(new(Guid.NewGuid(), orderId, order.Version, f.AdminPin))).Status);

        route.IsActive = true;
        var incomplete = await f.Db.ProductionRecipes.SingleAsync(x => x.IsActive);
        incomplete.IsActive = false;
        f.Stage.DefaultWipLocationId = f.WipA.Id;
        f.Db.ProductionRecipes.Add(new ProductionRecipe
        {
            ProductId = f.Finished.Id, Version = 3, BaseQuantity = 10, IsActive = true,
            Reason = "Lista completa", CreatedByUserId = admin.Id,
            Lines = { new ProductionRecipeLine { MaterialProductId = f.Material.Id, StageId = f.Stage.Id, Quantity = 5 } }
        });
        await f.Db.SaveChangesAsync();

        var reviewed = await f.Planning.ReviewAsync(new(Guid.NewGuid(), orderId, order.Version, [],
            "Completar fotografía", f.AdminPin));
        Assert.Equal(ProductionPlanningStatus.Success, reviewed.Status);
        f.Db.ChangeTracker.Clear();
        order = await f.Db.ProductionWorkOrders.Include(x => x.Stages).Include(x => x.MaterialPlan)
            .SingleAsync(x => x.Id == orderId);
        Assert.Single(order.Stages);
        Assert.Single(order.MaterialPlan);
        Assert.Equal(3, order.RecipeVersion);
        Assert.True(order.UsesBatchTraceability);
    }

    [Fact]
    public async Task Catalog_recipe_summary_uses_material_process_unique_and_unresolved_precedence()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.ProductionMaterialWipDefaults.Add(new() { ProductId = f.Material.Id, ProductionStageId = f.Stage.Id,
            LocationId = f.WipA.Id });
        await f.Db.SaveChangesAsync();

        var summary = await f.Planning.GetCatalogRecipeSummaryAsync(f.Finished.Id);
        var line = Assert.Single(summary!.Lines);
        Assert.True(summary.HasRecipe);
        Assert.Equal(1, summary.Version);
        Assert.Equal(10, summary.BaseQuantity);
        Assert.Equal("Configurado", line.Status);
        Assert.Equal("Predeterminado del material", line.ResolutionSource);
        Assert.Equal("WIP-A", line.Target);

        f.Db.ProductionMaterialWipDefaults.RemoveRange(await f.Db.ProductionMaterialWipDefaults.ToListAsync());
        f.Stage.DefaultWipLocationId = f.WipB.Id;
        await f.Db.SaveChangesAsync();
        line = Assert.Single((await f.Planning.GetCatalogRecipeSummaryAsync(f.Finished.Id))!.Lines);
        Assert.Equal("Predeterminado del proceso", line.ResolutionSource);
        Assert.Equal("WIP-B", line.Target);

        f.Stage.DefaultWipLocationId = null;
        var second = await f.Db.ProductionProcessWipTargets.SingleAsync(x => x.LocationId == f.WipB.Id);
        f.Db.ProductionProcessWipTargets.Remove(second);
        await f.Db.SaveChangesAsync();
        line = Assert.Single((await f.Planning.GetCatalogRecipeSummaryAsync(f.Finished.Id))!.Lines);
        Assert.Equal("Único destino del proceso", line.ResolutionSource);
        Assert.Equal("WIP-A", line.Target);

        f.Db.ProductionProcessWipTargets.Add(new() { ProductionStageId = f.Stage.Id, LocationId = f.WipB.Id });
        await f.Db.SaveChangesAsync();
        line = Assert.Single((await f.Planning.GetCatalogRecipeSummaryAsync(f.Finished.Id))!.Lines);
        Assert.Equal("Requiere destino", line.Status);
        Assert.Equal("Sin resolver", line.ResolutionSource);
        Assert.Equal("Sin destino automático", line.Target);
    }

    [Fact]
    public async Task Catalog_recipe_summary_marks_unassigned_material_as_incomplete()
    {
        await using var f = await Fixture.CreateAsync();
        var recipe = await f.Db.ProductionRecipes.Include(x => x.Lines).SingleAsync();
        Assert.Single(recipe.Lines).StageId = null;
        await f.Db.SaveChangesAsync();

        var summary = await f.Planning.GetCatalogRecipeSummaryAsync(f.Finished.Id);

        Assert.False(summary!.IsComplete);
        var line = Assert.Single(summary.Lines);
        Assert.Equal("Requiere etapa", line.Status);
        Assert.Equal("Pendiente de asignar", line.Process);
    }

    [Fact]
    public async Task Catalog_recipe_summary_preserves_invalid_priority_and_reports_inactive_material()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.ProductionMaterialWipDefaults.Add(new() { ProductId = f.Material.Id, ProductionStageId = f.Stage.Id,
            LocationId = f.WipA.Id });
        f.Stage.DefaultWipLocationId = f.WipB.Id;
        f.WipA.IsBlocked = true;
        await f.Db.SaveChangesAsync();

        var line = Assert.Single((await f.Planning.GetCatalogRecipeSummaryAsync(f.Finished.Id))!.Lines);
        Assert.Equal("Incompatible", line.Status);
        Assert.Equal("Predeterminado del material", line.ResolutionSource);
        Assert.Equal("WIP-A", line.Target);
        Assert.Contains("bloqueada", line.Warning!, StringComparison.OrdinalIgnoreCase);

        f.Material.IsActive = false;
        await f.Db.SaveChangesAsync();
        line = Assert.Single((await f.Planning.GetCatalogRecipeSummaryAsync(f.Finished.Id))!.Lines);
        Assert.Equal("Material inactivo", line.Status);
    }

    [Fact]
    public async Task Catalog_recipe_summary_returns_normal_empty_state_without_inventory_contracts()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.ProductionRecipes.RemoveRange(await f.Db.ProductionRecipes.Include(x => x.Lines).ToListAsync());
        await f.Db.SaveChangesAsync();

        var summary = await f.Planning.GetCatalogRecipeSummaryAsync(f.Finished.Id);
        Assert.NotNull(summary);
        Assert.False(summary.HasRecipe);
        Assert.Null(summary.Version);
        Assert.Empty(summary.Lines);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("stock", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reserv", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("movement", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await f.Planning.GetCatalogRecipeSummaryAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Catalog_recipe_summary_reads_the_active_recipe_when_the_panel_is_opened()
    {
        await using var f = await Fixture.CreateAsync();
        var previous = await f.Db.ProductionRecipes.SingleAsync();
        previous.IsActive = false;
        f.Db.ProductionRecipes.Add(new()
        {
            ProductId = f.Finished.Id,
            Version = 2,
            BaseQuantity = 20,
            Reason = "Receta actualizada antes de abrir el desplegable",
            CreatedByUserId = previous.CreatedByUserId,
            Lines = { new ProductionRecipeLine { MaterialProductId = f.Material.Id, StageId = f.Stage.Id, Quantity = 8 } }
        });
        await f.Db.SaveChangesAsync();

        var summary = await f.Planning.GetCatalogRecipeSummaryAsync(f.Finished.Id);

        Assert.Equal(2, summary!.Version);
        Assert.Equal(20, summary.BaseQuantity);
        Assert.Equal(8, Assert.Single(summary.Lines).Quantity);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public WarehouseDbContext Db { get; }
        public ProductionPlanningService Planning { get; }
        public ProductionService Production { get; }
        public Product Finished { get; }
        public Product Material { get; }
        public ProductionStage Stage { get; }
        public Location WipA { get; }
        public Location WipB { get; }
        public readonly string AdminPin = "4826";

        private Fixture(WarehouseDbContext db, ProductionPlanningService planning, ProductionService production,
            Product finished, Product material, ProductionStage stage, Location wipA, Location wipB) =>
            (Db, Planning, Production, Finished, Material, Stage, WipA, WipB) = (db, planning, production, finished, material, stage, wipA, wipB);

        public static async Task<Fixture> CreateAsync()
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await db.Database.EnsureCreatedAsync();
            var pins = new UserPinService(db, new PinProtector(Key));
            var admin = new User { FullName = "Admin P2", RoleId = 1, PinLookup = "", PinHash = "" };
            await pins.AssignAsync(admin, "4826");
            var finished = new Product { Sku = "PT-P2", Description = "Terminado P2", BaseUnitId = 1 };
            var material = new Product { Sku = "MP-P2", Description = "Material P2", BaseUnitId = 1 };
            var stage = new ProductionStage { Code = "P2", Name = "Proceso P2" };
            var wipA = new Location { Code = "WIP-A", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
            var wipB = new Location { Code = "WIP-B", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
            stage.WipTargets.Add(new() { Location = wipA });
            stage.WipTargets.Add(new() { Location = wipB });
            var route = new ProductionRoute { Product = finished, Name = "Ruta P2", Stages = { new ProductionRouteStage { Stage = stage, Sequence = 1 } } };
            var recipe = new ProductionRecipe { Product = finished, Version = 1, BaseQuantity = 10, Reason = "Receta P2", CreatedByUser = admin,
                Lines = { new ProductionRecipeLine { MaterialProduct = material, Stage = stage, Quantity = 5 } } };
            db.AddRange(admin, material, wipA, wipB, route, recipe);
            await db.SaveChangesAsync();
            var movements = new InventoryMovementService(db, pins, TimeProvider.System);
            var planning = new ProductionPlanningService(db, pins, TimeProvider.System);
            return new(db, planning, new ProductionService(db, pins, movements, TimeProvider.System), finished, material, stage, wipA, wipB);
        }

        public async Task<Guid> CreateOrderAsync()
        {
            var result = await Production.CreateOrderAsync(new(Guid.NewGuid(), Finished.Id, 20, null, null, null, AdminPin));
            Assert.Equal(ProductionCommandStatus.Success, result.Status);
            return result.WorkOrderId!.Value;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
