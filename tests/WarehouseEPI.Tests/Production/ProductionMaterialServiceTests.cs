using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed partial class ProductionMaterialServiceTests
{
    [Fact]
    public async Task Cancelling_idle_scheduled_order_reverses_issued_material_atomically()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(InventoryMovementStatus.Success, (await fixture.IssueAsync(5)).Status);
        var issued = await fixture.Db.ProductionMaterialIssueLinks.SingleAsync();
        Assert.Equal(ProductionMaterialStatus.Success, (await fixture.Materials.ApplyAsync(
            new ProductionMaterialCommand(Guid.NewGuid(), fixture.Order.Id, fixture.OrderStage.Id,
                fixture.Order.Version, ProductionMaterialOperationType.Consumption,
                [new ProductionMaterialSelection(issued.Id, 3)], fixture.OperatorPin))).Status);
        var day = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek
        {
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('W', 64),
            WeekStart = day, WeekEnd = day.AddDays(6), Status = ProductionScheduleWeekStatus.Open,
            CreatedByUserId = fixture.AdminId, CreatedAt = DateTimeOffset.UtcNow
        };
        week.Lines.Add(new ProductionScheduleLine
        {
            Sequence = 1, PlannedDate = day, ProductId = fixture.Order.ProductId,
            Quantity = 10, WorkOrderId = fixture.Order.Id
        });
        fixture.Db.Add(week);
        await fixture.Db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(fixture.Db, fixture.Pins,
            fixture.Movements, TimeProvider.System);
        var line = Assert.Single((await service.GetWeekAsync(week.Id))!.Lines);
        var eligibility = await service.GetDeletionEligibilityAsync(week.Id, [line.Id]);
        Assert.True(eligibility[line.Id].Allowed);
        Assert.True(eligibility[line.Id].RequiresPin);
        var command = new CancelProductionScheduleLineCommand(Guid.NewGuid(), week.Id, line.Id,
            week.Version, line.Version, fixture.AdminId, "0000");
        Assert.Equal(ProductionDailyCommandStatus.InvalidPin,
            (await service.CancelLineAsync(command)).Status);
        Assert.Equal(5, await fixture.Db.ProductionMaterialIssueLinks.SumAsync(x => x.Quantity));

        var result = await service.CancelLineAsync(command with
        {
            OperationId = Guid.NewGuid(), AdminPin = fixture.AdminPin
        });
        Assert.True(result.Success, string.Join(" | ", result.Errors ?? []));
        Assert.All(await fixture.Db.ProductionMaterialIssueLinks.ToListAsync(),
            x => Assert.Equal(x.Quantity, x.CancelledQuantity));
        Assert.Equal(ProductionWorkOrderStatus.Cancelled,
            (await fixture.Db.ProductionWorkOrders.SingleAsync(x => x.Id == fixture.Order.Id)).Status);
        Assert.Equal(10, await fixture.Db.InventoryBalances.Where(x => x.ProductId == fixture.Material.Id &&
            x.LocationId == fixture.Source.Id).SumAsync(x => x.Quantity));
        Assert.Empty((await service.GetWeekAsync(week.Id))!.Lines);
    }

    [Fact]
    public async Task Failed_delivery_reversal_keeps_scheduled_line_and_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        var delivery = await fixture.IssueAsync(5);
        Assert.Equal(InventoryMovementStatus.Success, delivery.Status);
        fixture.Db.InventoryMovementCorrections.Add(new InventoryMovementCorrection
        {
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('R', 64),
            Type = InventoryMovementCorrectionType.Reversal,
            OriginalMovementId = delivery.MovementId!.Value,
            ReversalMovementId = delivery.MovementId.Value,
            Reason = "Corrección previa", RequestedByUserId = fixture.AdminId,
            AuthorizedByUserId = fixture.AdminId
        });
        var day = new DateOnly(2026, 9, 21);
        var week = new ProductionScheduleWeek
        {
            OperationId = Guid.NewGuid(), RequestFingerprint = new string('W', 64),
            WeekStart = day, WeekEnd = day.AddDays(6), Status = ProductionScheduleWeekStatus.Open,
            CreatedByUserId = fixture.AdminId, CreatedAt = DateTimeOffset.UtcNow
        };
        week.Lines.Add(new ProductionScheduleLine
        {
            Sequence = 1, PlannedDate = day, ProductId = fixture.Order.ProductId,
            Quantity = 10, WorkOrderId = fixture.Order.Id
        });
        fixture.Db.Add(week);
        await fixture.Db.SaveChangesAsync();
        var service = new ProductionDailyScheduleService(fixture.Db, fixture.Pins,
            fixture.Movements, TimeProvider.System);
        var line = Assert.Single((await service.GetWeekAsync(week.Id))!.Lines);
        var result = await service.CancelLineAsync(new(Guid.NewGuid(), week.Id, line.Id,
            week.Version, line.Version, fixture.AdminId, fixture.AdminPin));
        Assert.Equal(ProductionDailyCommandStatus.ValidationFailed, result.Status);
        Assert.False((await fixture.Db.ProductionScheduleLines.AsNoTracking().SingleAsync(x => x.Id == line.Id)).IsCancelled);
        Assert.Equal(ProductionWorkOrderStatus.Released,
            (await fixture.Db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Id == fixture.Order.Id)).Status);
        Assert.Equal(5, await fixture.Db.ProductionMaterialIssueLinks.SumAsync(x => x.Quantity - x.CancelledQuantity));
    }

    [Fact]
    public async Task Linked_issue_and_partial_consumption_preserve_the_order_reservation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operationId = Guid.NewGuid();
        var issue = await fixture.IssueAsync(6, operationId);
        var retry = await fixture.IssueAsync(6, operationId);

        Assert.Equal(InventoryMovementStatus.Success, issue.Status);
        Assert.Equal(issue.MovementId, retry.MovementId);
        var link = Assert.Single(await fixture.Db.ProductionMaterialIssueLinks.ToListAsync());
        Assert.Equal(fixture.Order.Id, link.WorkOrderId);
        Assert.Equal(1u, fixture.Order.Version);

        var consumed = await fixture.Materials.ApplyAsync(new ProductionMaterialCommand(
            Guid.NewGuid(), fixture.Order.Id, fixture.OrderStage.Id, fixture.Order.Version,
            ProductionMaterialOperationType.Consumption,
            [new ProductionMaterialSelection(link.Id, 4)], fixture.OperatorPin));

        Assert.Equal(ProductionMaterialStatus.Success, consumed.Status);
        var row = Assert.Single(await fixture.Materials.GetIssuesAsync(fixture.Order.Id));
        Assert.Equal(6, row.Issued);
        Assert.Equal(4, row.Consumed);
        Assert.Equal(2, row.Pending);
        Assert.Equal(2, await fixture.Db.InventoryBalances
            .Where(x => x.ProductId == fixture.Material.Id && x.LocationId == fixture.Wip.Id)
            .SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Linked_issue_rejects_an_obsolete_order_version_before_moving_inventory()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Order.Version++;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.IssueAsync(2, expectedVersion: 0);

        Assert.Equal(InventoryMovementStatus.BalanceChanged, result.Status);
        Assert.Empty(await fixture.Db.ProductionMaterialIssueLinks.ToListAsync());
        Assert.Single(await fixture.Db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task General_wip_operation_cannot_take_quantity_reserved_for_an_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IssueAsync(6);

        var result = await fixture.Movements.ConfirmAsync(new InventoryMovementCommand(
            Guid.NewGuid(), InventoryMovementType.Exit, fixture.OperatorPin,
            [new InventoryMovementLineCommand(fixture.Material.Id, 1, SourceLocationId: fixture.Wip.Id)],
            Purpose: InventoryMovementPurpose.WipConsumption, OperationalAreaId: fixture.Wip.Id));

        Assert.Equal(InventoryMovementStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ValidationErrors, x => x.Contains("reservado", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(6, await fixture.Db.InventoryBalances
            .Where(x => x.ProductId == fixture.Material.Id && x.LocationId == fixture.Wip.Id)
            .SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Admin_reversal_restores_inventory_pending_and_order_version()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IssueAsync(5);
        var link = await fixture.Db.ProductionMaterialIssueLinks.SingleAsync();
        var consumed = await fixture.Materials.ApplyAsync(new ProductionMaterialCommand(
            Guid.NewGuid(), fixture.Order.Id, fixture.OrderStage.Id, fixture.Order.Version,
            ProductionMaterialOperationType.Consumption,
            [new ProductionMaterialSelection(link.Id, 3)], fixture.OperatorPin));
        var versionAfterConsumption = fixture.Order.Version;

        var reversed = await fixture.Materials.ReverseAsync(Guid.NewGuid(), consumed.OperationId!.Value,
            fixture.Order.Version, fixture.AdminPin, "Captura equivocada");

        Assert.Equal(ProductionMaterialStatus.Success, reversed.Status);
        Assert.Equal(versionAfterConsumption + 1, fixture.Order.Version);
        Assert.Equal(5, Assert.Single(await fixture.Materials.GetIssuesAsync(fixture.Order.Id)).Pending);
        Assert.Equal(5, await fixture.Db.InventoryBalances
            .Where(x => x.ProductId == fixture.Material.Id && x.LocationId == fixture.Wip.Id)
            .SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task New_link_rejects_a_process_not_effective_for_the_destination()
    {
        await using var fixture = await Fixture.CreateAsync(includeTarget: false);

        var result = await fixture.IssueAsync(2);

        Assert.Equal(InventoryMovementStatus.ValidationFailed, result.Status);
        Assert.Empty(await fixture.Db.ProductionMaterialIssueLinks.ToListAsync());
        Assert.Equal(10, await fixture.Db.InventoryBalances
            .Where(x => x.ProductId == fixture.Material.Id && x.LocationId == fixture.Source.Id)
            .SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Generic_correction_rejects_a_linked_material_operation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IssueAsync(4);
        var link = await fixture.Db.ProductionMaterialIssueLinks.SingleAsync();
        var consumed = await fixture.Materials.ApplyAsync(new ProductionMaterialCommand(Guid.NewGuid(),
            fixture.Order.Id, fixture.OrderStage.Id, fixture.Order.Version,
            ProductionMaterialOperationType.Consumption,
            [new ProductionMaterialSelection(link.Id, 1)], fixture.OperatorPin));
        var corrections = new InventoryCorrectionService(fixture.Db, fixture.Pins, fixture.Movements,
            TimeProvider.System);

        var result = await corrections.ConfirmAsync(new InventoryCorrectionCommand(Guid.NewGuid(),
            Assert.Single(consumed.MovementIds!), fixture.AdminId, fixture.AdminPin, "Corrección genérica"));

        Assert.Equal(InventoryCorrectionStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ValidationErrors, x => x.Contains("orden de trabajo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Linked_warehouse_return_moves_reserved_lots_and_reduces_pending()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IssueAsync(4);
        var link = await fixture.Db.ProductionMaterialIssueLinks.SingleAsync();

        var result = await fixture.Materials.ApplyAsync(new ProductionMaterialCommand(Guid.NewGuid(),
            fixture.Order.Id, fixture.OrderStage.Id, fixture.Order.Version,
            ProductionMaterialOperationType.WarehouseReturn,
            [new ProductionMaterialSelection(link.Id, 1)], fixture.OperatorPin,
            DestinationLocationId: fixture.Source.Id));

        Assert.Equal(ProductionMaterialStatus.Success, result.Status);
        var row = Assert.Single(await fixture.Materials.GetIssuesAsync(fixture.Order.Id));
        Assert.Equal(1, row.WarehouseReturned);
        Assert.Equal(3, row.Pending);
    }

    [Fact]
    public async Task Linked_supplier_return_requires_reference_and_reduces_pending_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IssueAsync(4);
        var link = await fixture.Db.ProductionMaterialIssueLinks.SingleAsync();
        var operationId = Guid.NewGuid();
        var command = new ProductionMaterialCommand(operationId, fixture.Order.Id, fixture.OrderStage.Id,
            fixture.Order.Version, ProductionMaterialOperationType.SupplierReturn,
            [new ProductionMaterialSelection(link.Id, 1)], fixture.OperatorPin, Reference: "RMA-01");

        var result = await fixture.Materials.ApplyAsync(command);
        var retry = await fixture.Materials.ApplyAsync(command);

        Assert.Equal(ProductionMaterialStatus.Success, result.Status);
        Assert.Equal(result.OperationId, retry.OperationId);
        var row = Assert.Single(await fixture.Materials.GetIssuesAsync(fixture.Order.Id));
        Assert.Equal(1, row.SupplierReturned);
        Assert.Equal(3, row.Pending);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        public WarehouseDbContext Db { get; }
        public InventoryMovementService Movements { get; }
        public ProductionMaterialService Materials { get; }
        public UserPinService Pins { get; }
        public Guid AdminId { get; }
        public Product Material { get; }
        public Location Source { get; }
        public Location Wip { get; }
        public ProductionWorkOrder Order { get; }
        public ProductionWorkOrderStage OrderStage { get; }
        public readonly string AdminPin = "4826";
        public readonly string OperatorPin = "5937";

        private Fixture(WarehouseDbContext db, UserPinService pins, Guid adminId, InventoryMovementService movements,
            ProductionMaterialService materials, Product material, Location source, Location wip,
            ProductionWorkOrder order, ProductionWorkOrderStage orderStage)
            => (Db, Pins, AdminId, Movements, Materials, Material, Source, Wip, Order, OrderStage) =
                (db, pins, adminId, movements, materials, material, source, wip, order, orderStage);

        public static async Task<Fixture> CreateAsync(bool includeTarget = true)
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await db.Database.EnsureCreatedAsync();
            var pins = new UserPinService(db, new PinProtector(Key));
            var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "", PinHash = "" };
            var user = new User { FullName = "Operador", RoleId = 2, PinLookup = "", PinHash = "" };
            await pins.AssignAsync(admin, "4826");
            await pins.AssignAsync(user, "5937");
            var material = new Product { Sku = "MP-01", Description = "Materia prima", BaseUnitId = 1 };
            var finished = new Product { Sku = "PT-01", Description = "Producto terminado", BaseUnitId = 1 };
            var source = new Location { Code = "A-1-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 1, PalletNumber = 1 };
            var wip = new Location { Code = "WIP-2", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
            var process = new ProductionStage { Code = "COS", Name = "Costura" };
            var order = new ProductionWorkOrder
            {
                CreateOperationId = Guid.NewGuid(), CreateFingerprint = "test", Number = "OT-0001",
                Product = finished, UnitId = 1, TargetQuantity = 10, AuthorizedQuantity = 10,
                Status = ProductionWorkOrderStatus.Released, CreatedByUser = admin
            };
            var orderStage = new ProductionWorkOrderStage
            {
                WorkOrder = order, SourceStage = process, Sequence = 1, Code = process.Code, Name = process.Name
            };
            order.Stages.Add(orderStage);
            if (includeTarget)
                process.WipTargets.Add(new ProductionProcessWipTarget { Location = wip });
            db.AddRange(admin, user, material, source, wip, process, order);
            await db.SaveChangesAsync();
            var movements = new InventoryMovementService(db, pins, TimeProvider.System);
            var entry = await movements.ConfirmAsync(new InventoryMovementCommand(Guid.NewGuid(),
                InventoryMovementType.Entry, "5937",
                [new InventoryMovementLineCommand(material.Id, 10, DestinationLocationId: source.Id)]));
            Assert.Equal(InventoryMovementStatus.Success, entry.Status);
            return new Fixture(db, pins, admin.Id, movements,
                new ProductionMaterialService(db, pins, movements, TimeProvider.System),
                material, source, wip, order, orderStage);
        }

        public Task<InventoryMovementResult> IssueAsync(decimal quantity, Guid? operationId = null,
            uint? expectedVersion = null) => Materials.IssueAsync(
            new InventoryMovementCommand(operationId ?? Guid.NewGuid(), InventoryMovementType.Transfer, OperatorPin,
                [new InventoryMovementLineCommand(Material.Id, quantity, SourceLocationId: Source.Id,
                    DestinationLocationId: Wip.Id)], Purpose: InventoryMovementPurpose.ProductionIssue,
                OperationalAreaId: Wip.Id), Order.Id, OrderStage.Id, expectedVersion ?? Order.Version);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
