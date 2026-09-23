using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed partial class ProductionSupplyServiceTests
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

    [Fact]
    public async Task Guided_preparation_is_saved_and_can_be_recovered()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 10);
        await fixture.CreateAndReleaseAsync(10);
        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());
        var detail = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var source = Assert.Single(detail.Sources, x => x.Kind == ProductionSupplySourceKind.Warehouse);

        var saved = await fixture.Preparations.SaveAsync(new(Guid.NewGuid(), row.LineId, row.RequestVersion,
            detail.DestinationLocationId, null, 0, [new(ProductionSupplySourceKind.Warehouse, source.LocationId, 4)], fixture.OperatorPin));
        var recovered = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));

        Assert.Equal(ProductionSupplyCommandStatus.Success, saved.Status);
        Assert.NotNull(recovered.PreparationId);
        Assert.Equal(4, Assert.Single(recovered.Selected).Quantity);
        Assert.Equal("Operador P3", recovered.PreparedBy);
        Assert.Empty(fixture.Db.InventoryMovements);
    }

    [Fact]
    public async Task Guided_preparation_persists_the_automatic_plate_composition_used_by_confirmation()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 10);
        var first = new PalletPlate { ProductId = fixture.Material.Id, LocationId = fixture.Source.Id,
            Quantity = 3, Version = 1, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2) };
        first.Lots.Add(new() { Plate = first, LotId = fixture.MaterialLot.Id, Quantity = 3 });
        var second = new PalletPlate { ProductId = fixture.Material.Id, LocationId = fixture.Source.Id,
            Quantity = 4, Version = 1, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        second.Lots.Add(new() { Plate = second, LotId = fixture.MaterialLot.Id, Quantity = 4 });
        fixture.Db.PalletPlates.AddRange(first, second);
        await fixture.Db.SaveChangesAsync();
        await fixture.CreateAndReleaseAsync(5);
        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());
        var detail = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var source = Assert.Single(detail.Sources, x => x.Kind == ProductionSupplySourceKind.Warehouse);

        var saved = await fixture.Preparations.SaveAsync(new(Guid.NewGuid(), row.LineId, row.RequestVersion,
            detail.DestinationLocationId, null, 0, [new(ProductionSupplySourceKind.Warehouse, source.LocationId, 5)], fixture.OperatorPin));
        Assert.Equal(ProductionSupplyCommandStatus.Success, saved.Status);
        var prepared = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var selection = Assert.Single(prepared.Selected);
        Assert.Equal(new[] { (first.Id, 3m), (second.Id, 2m) }, selection.Plates!.Select(x => (x.PlateId, x.Quantity)).ToArray());

        second.CreatedAt = first.CreatedAt.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();
        var confirmed = await fixture.Preparations.ConfirmAsync(new(Guid.NewGuid(), prepared.PreparationId!.Value,
            prepared.PreparationVersion, prepared.Line.RequestVersion, fixture.OperatorPin));

        Assert.Equal(ProductionSupplyCommandStatus.Success, confirmed.Status);
        Assert.Equal(fixture.Wip.Id, (await fixture.Db.PalletPlates.SingleAsync(x => x.Id == first.Id)).LocationId);
        Assert.Equal(3, (await fixture.Db.PalletPlates.SingleAsync(x => x.Id == first.Id)).Quantity);
        Assert.Equal(fixture.Source.Id, (await fixture.Db.PalletPlates.SingleAsync(x => x.Id == second.Id)).LocationId);
        Assert.Equal(2, (await fixture.Db.PalletPlates.SingleAsync(x => x.Id == second.Id)).Quantity);
    }

    [Fact]
    public async Task Two_tablets_cannot_overwrite_the_same_saved_preparation()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 10);
        await fixture.CreateAndReleaseAsync(10);
        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());
        var detail = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var source = Assert.Single(detail.Sources, x => x.Kind == ProductionSupplySourceKind.Warehouse);
        Assert.Equal(ProductionSupplyCommandStatus.Success, (await fixture.Preparations.SaveAsync(new(Guid.NewGuid(), row.LineId,
            row.RequestVersion, detail.DestinationLocationId, null, 0, [new(ProductionSupplySourceKind.Warehouse, source.LocationId, 4)], fixture.OperatorPin))).Status);
        var tabletA = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var tabletB = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        Assert.Equal(ProductionSupplyCommandStatus.Success, (await fixture.Preparations.SaveAsync(new(Guid.NewGuid(), row.LineId,
            tabletA.Line.RequestVersion, tabletA.DestinationLocationId, tabletA.PreparationId, tabletA.PreparationVersion,
            [new(ProductionSupplySourceKind.Warehouse, source.LocationId, 5)], fixture.OperatorPin))).Status);

        var stale = await fixture.Preparations.SaveAsync(new(Guid.NewGuid(), row.LineId, tabletB.Line.RequestVersion,
            tabletB.DestinationLocationId, tabletB.PreparationId, tabletB.PreparationVersion,
            [new(ProductionSupplySourceKind.Warehouse, source.LocationId, 6)], fixture.OperatorPin));

        Assert.Equal(ProductionSupplyCommandStatus.ConcurrencyConflict, stale.Status);
    }

    [Fact]
    public async Task Confirmation_can_reduce_the_actual_quantity_without_changing_the_saved_origin()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 10);
        await fixture.CreateAndReleaseAsync(10);
        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());
        var detail = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var source = Assert.Single(detail.Sources, x => x.Kind == ProductionSupplySourceKind.Warehouse);
        await fixture.Preparations.SaveAsync(new(Guid.NewGuid(), row.LineId, row.RequestVersion, detail.DestinationLocationId,
            null, 0, [new(ProductionSupplySourceKind.Warehouse, source.LocationId, 6)], fixture.OperatorPin));
        var prepared = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var operationId = Guid.NewGuid();

        var confirmed = await fixture.Preparations.ConfirmAsync(new(operationId, prepared.PreparationId!.Value,
            prepared.PreparationVersion, prepared.Line.RequestVersion, fixture.OperatorPin,
            [new(ProductionSupplySourceKind.Warehouse, source.LocationId, 4)]));

        Assert.Equal(ProductionSupplyCommandStatus.Success, confirmed.Status);
        Assert.Equal(4, (await fixture.Db.ProductionSupplyConfirmations.SingleAsync()).Quantity);
        Assert.Equal(6, Assert.Single(await fixture.Supplies.GetQueueAsync()).Pending);
        Assert.Equal(ProductionSupplyCommandStatus.Success, (await fixture.Preparations.GetResultAsync(operationId))!.Status);
        var proof = await fixture.Preparations.GetConfirmationResultAsync(operationId);
        Assert.NotNull(proof);
        Assert.Equal(4, proof.ConfirmedQuantity);
        Assert.Equal(6, proof.PendingQuantity);
        Assert.Equal(1, proof.MovementCount);
        Assert.Equal(0, proof.WipAssignmentCount);
    }

    [Fact]
    public async Task Guided_confirmation_combines_warehouse_and_existing_wip_without_fictitious_transfer()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 10);
        await fixture.AddWipStockAsync(2);
        await fixture.CreateAndReleaseAsync(10);
        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());
        var detail = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var warehouse = Assert.Single(detail.Sources, x => x.Kind == ProductionSupplySourceKind.Warehouse);
        var wip = Assert.Single(detail.Sources, x => x.Kind == ProductionSupplySourceKind.ExistingWip);
        var save = await fixture.Preparations.SaveAsync(new(Guid.NewGuid(), row.LineId, row.RequestVersion,
            detail.DestinationLocationId, null, 0,
            [new(ProductionSupplySourceKind.Warehouse, warehouse.LocationId, 3), new(ProductionSupplySourceKind.ExistingWip, wip.LocationId, 2)], fixture.OperatorPin));
        Assert.Equal(ProductionSupplyCommandStatus.Success, save.Status);
        var prepared = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var operationId = Guid.NewGuid();

        var confirmed = await fixture.Preparations.ConfirmAsync(new(operationId, prepared.PreparationId!.Value,
            prepared.PreparationVersion, prepared.Line.RequestVersion, fixture.OperatorPin));
        var repeated = await fixture.Preparations.ConfirmAsync(new(operationId, prepared.PreparationId.Value,
            prepared.PreparationVersion, prepared.Line.RequestVersion, fixture.OperatorPin));

        Assert.Equal(ProductionSupplyCommandStatus.Success, confirmed.Status);
        Assert.Equal(ProductionSupplyCommandStatus.Success, repeated.Status);
        var remaining = Assert.Single(await fixture.Supplies.GetQueueAsync());
        Assert.Equal(5, remaining.Delivered);
        Assert.Equal(5, remaining.Pending);
        Assert.Equal(2, await fixture.Db.ProductionMaterialIssueLinks.CountAsync());
        Assert.Equal(1, await fixture.Db.InventoryMovements.CountAsync());
        Assert.Equal(1, await fixture.Db.ProductionMaterialIssueLinks.CountAsync(x => x.Source == ProductionMaterialSupplySource.WipAssignment));
        var confirmation = await fixture.Db.ProductionSupplyConfirmations.Include(x => x.Movements).Include(x => x.Issues).SingleAsync();
        Assert.Equal(operationId, confirmation.OperationId);
        Assert.Single(confirmation.Movements);
        Assert.Equal(2, confirmation.Issues.Count);

        var issue = await fixture.Db.ProductionMaterialIssueLinks.FirstAsync(x => x.Source == ProductionMaterialSupplySource.WipAssignment);
        var order = await fixture.Db.ProductionWorkOrders.SingleAsync();
        var returned = await fixture.Materials.ApplyAsync(new ProductionMaterialCommand(Guid.NewGuid(), order.Id,
            issue.WorkOrderStageId, order.Version, ProductionMaterialOperationType.WarehouseReturn,
            [new(issue.Id, 1)], fixture.OperatorPin, fixture.Source.Id, Notes: "Reponer material devuelto",
            ReturnEffect: ProductionMaterialReturnEffect.Replenish));

        Assert.Equal(ProductionMaterialStatus.Success, returned.Status);
        var reopened = Assert.Single(await fixture.Supplies.GetQueueAsync());
        Assert.Equal(6, reopened.Pending);
    }

    [Fact]
    public async Task Admin_can_partially_cancel_unused_wip_assignment_without_changing_physical_stock()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 0);
        await fixture.AddWipStockAsync(3);
        await fixture.CreateAndReleaseAsync(3);
        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());
        var detail = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        var wip = Assert.Single(detail.Sources, x => x.Kind == ProductionSupplySourceKind.ExistingWip);
        Assert.Equal(ProductionSupplyCommandStatus.Success, (await fixture.Preparations.SaveAsync(new(Guid.NewGuid(), row.LineId,
            row.RequestVersion, detail.DestinationLocationId, null, 0, [new(ProductionSupplySourceKind.ExistingWip, wip.LocationId, 3)], fixture.OperatorPin))).Status);
        var prepared = Assert.IsType<ProductionSupplyPreparationView>(await fixture.Preparations.GetAsync(row.LineId));
        Assert.Equal(ProductionSupplyCommandStatus.Success, (await fixture.Preparations.ConfirmAsync(new(Guid.NewGuid(), prepared.PreparationId!.Value,
            prepared.PreparationVersion, prepared.Line.RequestVersion, fixture.OperatorPin))).Status);
        var issue = await fixture.Db.ProductionMaterialIssueLinks.SingleAsync();
        var completed = await fixture.Db.ProductionSupplyRequestLines.Include(x => x.SupplyRequest).SingleAsync();

        var cancelled = await fixture.Preparations.CancelWipAssignmentAsync(new(Guid.NewGuid(), completed.Id, issue.Id,
            completed.SupplyRequest.Version, 1, fixture.AdminPin, "Material asignado por error"));

        Assert.Equal(ProductionSupplyCommandStatus.Success, cancelled.Status);
        var reopened = Assert.Single(await fixture.Supplies.GetQueueAsync());
        Assert.Equal(1, reopened.Pending);
        Assert.Equal(2, reopened.Delivered);
        Assert.Equal(3, await fixture.Db.InventoryBalances.Where(x => x.LocationId == fixture.Wip.Id).SumAsync(x => x.Quantity));
        Assert.Equal(1, await fixture.Db.ProductionMaterialIssueLinks.Select(x => x.CancelledQuantity).SingleAsync());
    }

    [Fact]
    public async Task Admin_destination_change_is_stored_only_on_selected_material_line()
    {
        await using var fixture = await Fixture.CreateAsync(stock: 2);
        await fixture.CreateAndReleaseAsync(2);
        var row = Assert.Single(await fixture.Supplies.GetQueueAsync());
        var otherWip = new Location { Code = "WIP-CORTE-2", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var stageId = await fixture.Db.ProductionWorkOrderStages.Select(x => x.SourceStageId).SingleAsync();
        fixture.Db.Add(otherWip);
        fixture.Db.ProductionProcessWipTargets.Add(new ProductionProcessWipTarget { ProductionStageId = stageId, Location = otherWip });
        await fixture.Db.SaveChangesAsync();

        var changed = await fixture.Preparations.ChangeDestinationAsync(new(Guid.NewGuid(), row.LineId, row.RequestVersion,
            otherWip.Id, fixture.AdminPin, "Usar la segunda estación"));

        Assert.Equal(ProductionSupplyCommandStatus.Success, changed.Status);
        var line = await fixture.Db.ProductionSupplyRequestLines.Include(x => x.SupplyRequest).SingleAsync();
        Assert.Equal(otherWip.Id, line.DestinationLocationId);
        Assert.Equal("WIP-CORTE-2", line.DestinationCode);
        Assert.Equal(fixture.Wip.Id, line.SupplyRequest.DestinationLocationId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        public string AdminPin { get; } = "4826";
        public string OperatorPin { get; } = "5937";
        public WarehouseDbContext Db { get; }
        public ProductionService Production { get; }
        public ProductionSupplyService Supplies { get; }
        public ProductionSupplyPreparationService Preparations { get; }
        public ProductionMaterialService Materials { get; }
        public InventoryMovementService Movements { get; }
        public Product FinishedProduct { get; private set; } = null!;
        public Product Material { get; private set; } = null!;
        public Location Source { get; private set; } = null!;
        public Location Wip { get; private set; } = null!;
        public ProductLot MaterialLot { get; private set; } = null!;

        private Fixture(WarehouseDbContext db, UserPinService pins)
        {
            Db = db;
            Movements = new(db, pins, TimeProvider.System);
            Supplies = new(db, pins, TimeProvider.System);
            Preparations = new(db, pins, Movements, TimeProvider.System);
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
            fixture.MaterialLot = new ProductLot { Product = fixture.Material, Number = "MP-P3-01", NormalizedNumber = "MP-P3-01", LotDate = new DateOnly(2026, 9, 1) };
            db.Add(fixture.MaterialLot); await db.SaveChangesAsync();
            db.InventoryBalances.Add(new InventoryBalance { ProductId = fixture.Material.Id, LocationId = fixture.Source.Id, LotId = fixture.MaterialLot.Id, Quantity = stock });
            await db.SaveChangesAsync(); return fixture;
        }

        public async Task AddWipStockAsync(decimal quantity)
        {
            Db.InventoryBalances.Add(new InventoryBalance { ProductId = Material.Id, LocationId = Wip.Id,
                LotId = MaterialLot.Id, Quantity = quantity });
            await Db.SaveChangesAsync();
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
