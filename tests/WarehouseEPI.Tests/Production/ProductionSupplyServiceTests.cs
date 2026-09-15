using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionSupplyServiceTests
{
    [Fact]
    public async Task Release_creates_one_request_and_reserves_only_free_stock()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 6);
        var orderId = await fixture.CreateAndReleaseAsync(10);

        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());
        Assert.Equal(orderId, row.WorkOrderId);
        Assert.Equal(10, row.Required);
        Assert.Equal(6, row.Reserved);
        Assert.Equal(4, row.Shortage);
        Assert.Equal(1, await fixture.Supplies.GetPendingOrderCountAsync());
    }

    [Fact]
    public async Task Linked_delivery_can_pass_stock_negative_but_cannot_exceed_request_pending()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 6);
        await fixture.CreateAndReleaseAsync(10);
        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());

        var delivered = await fixture.Materials.IssueAsync(fixture.IssueCommand(row, 8), row.WorkOrderId,
            row.WorkOrderStageId, row.WorkOrderVersion, row.LineId, row.RequestVersion);
        var excessive = await fixture.Materials.IssueAsync(fixture.IssueCommand(row, 3), row.WorkOrderId,
            row.WorkOrderStageId, row.WorkOrderVersion + 1, row.LineId, row.RequestVersion + 1);

        Assert.Equal(InventoryMovementStatus.Success, delivered.Status);
        Assert.True(delivered.HasNegativeBalance);
        Assert.Equal(InventoryMovementStatus.ValidationFailed, excessive.Status);
        Assert.Contains(excessive.ValidationErrors, x => x.Contains("pendiente", StringComparison.OrdinalIgnoreCase));
        var remaining = Assert.Single(await fixture.Supplies.GetQueueAsync());
        Assert.Equal(2, remaining.Pending);
        Assert.Equal(0, remaining.Reserved);
    }

    [Fact]
    public async Task General_exit_cannot_use_stock_reserved_for_a_production_order()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 6);
        await fixture.CreateAndReleaseAsync(4);

        var result = await fixture.Movements.ConfirmAsync(new InventoryMovementCommand(Guid.NewGuid(),
            InventoryMovementType.Exit, fixture.OperatorPin,
            [new InventoryMovementLineCommand(fixture.Material.Id, 3, SourceLocationId: fixture.Source.Id)]));

        Assert.Equal(InventoryMovementStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ValidationErrors, x => x.Contains("otra orden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Admin_cancellation_releases_only_the_cancelled_pending_quantity()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 10);
        await fixture.CreateAndReleaseAsync(10);
        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());

        var result = await fixture.Supplies.CancelAsync(new(Guid.NewGuid(), row.LineId, row.RequestVersion,
            3, fixture.AdminPin, "La orden requiere menos material"));

        Assert.Equal(ProductionSupplyCommandStatus.Success, result.Status);
        var remaining = Assert.Single(await fixture.Supplies.GetQueueAsync());
        Assert.Equal(7, remaining.Pending);
        Assert.Equal(7, remaining.Reserved);
        Assert.Equal(3, remaining.Cancelled);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        public string AdminPin { get; } = "4826";
        public string OperatorPin { get; } = "5937";
        public WarehouseDbContext Db { get; }
        public ProductionService Production { get; }
        public ProductionSupplyService Supplies { get; }
        public ProductionMaterialService Materials { get; }
        public InventoryMovementService Movements { get; }
        public Product FinishedProduct { get; private set; } = null!;
        public Product Material { get; private set; } = null!;
        public Location Source { get; private set; } = null!;
        public Location Wip { get; private set; } = null!;

        private Fixture(WarehouseDbContext db, UserPinService pins)
        {
            Db = db;
            Movements = new(db, pins, TimeProvider.System);
            Supplies = new(db, pins, TimeProvider.System);
            Materials = new(db, pins, Movements, TimeProvider.System, Supplies);
            Production = new(db, pins, Movements, TimeProvider.System);
        }

        public static async Task<Fixture> CreateAsync(decimal stock)
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await db.Database.EnsureCreatedAsync();
            var pins = new UserPinService(db, new PinProtector(Key));
            var admin = new User { FullName = "Admin P3", RoleId = 1, PinLookup = string.Empty, PinHash = string.Empty };
            var op = new User { FullName = "Operador P3", RoleId = 2, PinLookup = string.Empty, PinHash = string.Empty };
            await pins.AssignAsync(admin, "4826"); await pins.AssignAsync(op, "5937"); db.Users.AddRange(admin, op);
            var fixture = new Fixture(db, pins);
            fixture.Source = new Location { Code = "A-01-01", Kind = LocationKind.Rack };
            fixture.Wip = new Location { Code = "WIP-CORTE", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
            fixture.FinishedProduct = new Product { Sku = "PT-P3", Description = "Terminado P3", BaseUnitId = 1 };
            fixture.Material = new Product { Sku = "MP-P3", Description = "Material P3", BaseUnitId = 1, DefaultEntryLocation = fixture.Source };
            var stage = new ProductionStage { Code = "CORTE-P3", Name = "Corte P3" };
            stage.WipTargets.Add(new ProductionProcessWipTarget { Location = fixture.Wip });
            db.AddRange(fixture.FinishedProduct, fixture.Material, fixture.Source, fixture.Wip, stage);
            await db.SaveChangesAsync();
            var route = await fixture.Production.CreateRouteAsync(new(Guid.NewGuid(), fixture.FinishedProduct.Id,
                "Ruta P3", [stage.Id], fixture.AdminPin));
            Assert.Equal(ProductionCommandStatus.Success, route.Status);
            db.ProductionRecipes.Add(new ProductionRecipe { ProductId = fixture.FinishedProduct.Id, Version = 1,
                BaseQuantity = 1, Reason = "Receta P3", CreatedByUserId = admin.Id,
                Lines = { new ProductionRecipeLine { MaterialProductId = fixture.Material.Id, StageId = stage.Id, Quantity = 1 } } });
            var lot = new ProductLot { Product = fixture.Material, Number = "MP-P3-01", NormalizedNumber = "MP-P3-01", LotDate = new DateOnly(2026, 9, 1) };
            db.Add(lot); await db.SaveChangesAsync();
            db.InventoryBalances.Add(new InventoryBalance { ProductId = fixture.Material.Id, LocationId = fixture.Source.Id, LotId = lot.Id, Quantity = stock });
            await db.SaveChangesAsync(); return fixture;
        }

        public async Task<Guid> CreateAndReleaseAsync(decimal quantity)
        {
            var created = await Production.CreateOrderAsync(new(Guid.NewGuid(), FinishedProduct.Id, quantity, null,
                new DateOnly(2026, 9, 20), null, AdminPin));
            Assert.Equal(ProductionCommandStatus.Success, created.Status);
            var released = await Production.ReleaseAsync(new(Guid.NewGuid(), created.WorkOrderId!.Value, null, AdminPin));
            Assert.True(released.Status == ProductionCommandStatus.Success, string.Join(" | ", released.ValidationErrors));
            return created.WorkOrderId.Value;
        }

        public InventoryMovementCommand IssueCommand(ProductionSupplyQueueRow row, decimal quantity) => new(Guid.NewGuid(),
            InventoryMovementType.Transfer, OperatorPin,
            [new InventoryMovementLineCommand(Material.Id, quantity, SourceLocationId: Source.Id, DestinationLocationId: Wip.Id)],
            row.WorkOrderNumber, "Surtimiento P3", Purpose: InventoryMovementPurpose.ProductionIssue, OperationalAreaId: Wip.Id);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
