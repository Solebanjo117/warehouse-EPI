using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Production;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class ProductionPlanningPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Planning_snapshot_persists_and_release_allows_an_inventory_warning()
    {
        await using var db = fixture.CreateDbContext();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var pins = new UserPinService(db, new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="));
        var admin = new User { FullName = $"Admin P2 {suffix}", RoleId = 1, PinLookup = "", PinHash = "" };
        await pins.AssignAsync(admin, "8642");
        var material = new Product { Sku = $"P2-MP-{suffix}", BaseUnitId = 1 };
        var finished = new Product { Sku = $"P2-PT-{suffix}", BaseUnitId = 1 };
        var wip = new Location { Code = $"P2-W-{suffix}", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var stage = new ProductionStage { Code = $"P2-{suffix}", Name = $"Proceso P2 {suffix}" };
        stage.WipTargets.Add(new() { Location = wip });
        var route = new ProductionRoute { Product = finished, Name = "Ruta P2", Stages = { new ProductionRouteStage { Stage = stage, Sequence = 1 } } };
        var recipe = new ProductionRecipe
        {
            Product = finished,
            Version = 1,
            BaseQuantity = 10,
            Reason = "Receta P2",
            CreatedByUser = admin,
            Lines = { new ProductionRecipeLine { MaterialProduct = material, Stage = stage, Quantity = 4 } }
        };
        db.AddRange(admin, material, wip, route, recipe);
        await db.SaveChangesAsync();

        var production = new ProductionService(db, pins, new InventoryMovementService(db, pins, TimeProvider.System), TimeProvider.System);
        var created = await production.CreateOrderAsync(new(Guid.NewGuid(), finished.Id, 20, null, null, null, "8642"));
        var order = await db.ProductionWorkOrders.SingleAsync(x => x.Id == created.WorkOrderId);
        var plan = await db.ProductionOrderMaterialPlans.SingleAsync(x => x.WorkOrderId == order.Id);
        Assert.Equal(wip.Id, plan.WipLocationId);
        Assert.Equal(ProductionWipResolutionSource.SingleProcessTarget, plan.WipResolutionSource);

        var view = await new ProductionPlanningService(db, pins, TimeProvider.System).GetAsync(order.Id);
        Assert.True(view.CanRelease);
        Assert.NotEmpty(view.Warnings);
        var released = await production.ReleaseAsync(new(Guid.NewGuid(), order.Id, order.Version, "8642"));
        Assert.Equal(ProductionCommandStatus.Success, released.Status);
    }

    [Fact]
    public async Task Existing_order_survives_P2_migration_down_and_up_without_backfill()
    {
        await using var db = fixture.CreateDbContext();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var user = new User { FullName = $"Admin legado P2 {suffix}", RoleId = 1, PinLookup = $"p2-{suffix}", PinHash = "legacy" };
        var product = new Product { Sku = $"P2-LEG-{suffix}", BaseUnitId = 1 };
        var stage = new ProductionStage { Code = $"P2L-{suffix}", Name = "Proceso legado P2" };
        var order = new ProductionWorkOrder
        {
            CreateOperationId = Guid.NewGuid(),
            CreateFingerprint = new('a', 64),
            Number = $"OT-P2-{suffix}",
            Product = product,
            UnitId = 1,
            TargetQuantity = 1,
            AuthorizedQuantity = 1,
            CreatedByUser = user,
            Status = ProductionWorkOrderStatus.Released
        };
        order.Stages.Add(new() { SourceStage = stage, Sequence = 1, Code = stage.Code, Name = stage.Name });
        db.Add(order);
        await db.SaveChangesAsync();
        var orderId = order.Id;
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync("20260911184026_MaterialWipDefaults");
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var restored = await db.ProductionWorkOrders.SingleAsync(x => x.Id == orderId);
        Assert.Equal(ProductionWorkOrderStatus.Released, restored.Status);
        Assert.Empty(await db.ProductionOrderMaterialPlans.Where(x => x.WorkOrderId == orderId).ToListAsync());
        Assert.True(await db.Database.CanConnectAsync());
    }

    [Fact]
    public async Task Recipe_draft_migration_down_fails_safely_when_an_unassigned_material_exists()
    {
        await using var db = fixture.CreateDbContext();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var admin = new User
        {
            FullName = $"Admin borrador {suffix}",
            RoleId = 1,
            PinLookup = $"draft-{suffix}",
            PinHash = "migration-test"
        };
        var finished = new Product { Sku = $"DRAFT-PT-{suffix}", BaseUnitId = 1 };
        var material = new Product { Sku = $"DRAFT-MP-{suffix}", BaseUnitId = 1 };
        var recipe = new ProductionRecipe
        {
            Product = finished,
            Version = 1,
            BaseQuantity = 1,
            Reason = "Borrador para descenso seguro",
            CreatedByUser = admin,
            Lines = { new ProductionRecipeLine { MaterialProduct = material, Quantity = 1 } }
        };
        db.AddRange(admin, material, recipe);
        await db.SaveChangesAsync();

        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.MigrateAsync("20260914122652_Phase132ProductionPlanningSnapshot"));
            Assert.Contains("materiales sin etapa", error.MessageText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("20260914152059_RecipeDraftMaterialStages", await db.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Database.MigrateAsync();
            var storedRecipe = await db.ProductionRecipes.Include(x => x.Lines).SingleAsync(x => x.Id == recipe.Id);
            db.ProductionRecipes.Remove(storedRecipe);
            db.Products.RemoveRange(await db.Products.Where(x => x.Id == finished.Id || x.Id == material.Id).ToListAsync());
            db.Users.Remove(await db.Users.SingleAsync(x => x.Id == admin.Id));
            await db.SaveChangesAsync();
        }
    }
}
