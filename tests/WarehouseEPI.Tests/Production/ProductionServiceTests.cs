using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionServiceTests
{
    [Fact]
    public async Task Partial_handoff_allows_received_quantity_to_advance_and_keeps_difference_pending()
    {
        await using var f=await Fixture.CreateAsync(2);
        var order=await f.CreateAndReleaseAsync(100);
        var stages=await f.Db.ProductionWorkOrderStages.OrderBy(x=>x.Sequence).ToArrayAsync();
        var process=await f.Service.ProcessAsync(new(Guid.NewGuid(),order,stages[0].Id,f.Shift.Id,false,100,100,0,0,null,f.OperatorPin));
        var delivered=await f.Service.DeliverAsync(new(Guid.NewGuid(),order,stages[0].Id,stages[1].Id,100,f.OperatorPin));
        var received=await f.Service.ReceiveAsync(new(Guid.NewGuid(),order,stages[0].Id,stages[1].Id,98,f.OperatorPin));

        Assert.Equal(ProductionCommandStatus.Success,process.Status);
        Assert.Equal(ProductionCommandStatus.Success,delivered.Status);
        Assert.Equal(ProductionCommandStatus.Success,received.Status);
        var detail=await new ProductionQueryService(f.Db).GetAsync(order);
        Assert.Equal(2,detail!.Stages[0].PendingReceipt);
        Assert.Equal(98,detail.Stages[1].AvailableInput);
    }

    [Fact]
    public async Task Rework_is_reclassified_without_counting_the_same_piece_twice()
    {
        await using var f=await Fixture.CreateAsync(1);var order=await f.CreateAndReleaseAsync(10);var stage=await f.Db.ProductionWorkOrderStages.SingleAsync();
        await f.Service.ProcessAsync(new(Guid.NewGuid(),order,stage.Id,f.Shift.Id,false,10,7,3,0,"Revisión",f.OperatorPin));
        var reworked=await f.Service.ProcessAsync(new(Guid.NewGuid(),order,stage.Id,f.Shift.Id,true,3,2,0,1,"Retrabajo terminado",f.OperatorPin));
        var detail=await new ProductionQueryService(f.Db).GetAsync(order);
        Assert.Equal(ProductionCommandStatus.Success,reworked.Status);Assert.Equal(9,detail!.Stages[0].Good);Assert.Equal(0,detail.Stages[0].Rework);Assert.Equal(1,detail.Stages[0].Scrap);Assert.Equal(10,detail.Stages[0].Processed);
    }

    [Fact]
    public async Task Warehouse_receipt_creates_one_linked_inventory_entry_and_retry_is_idempotent()
    {
        await using var f=await Fixture.CreateAsync(1);var order=await f.CreateAndReleaseAsync(5);var stage=await f.Db.ProductionWorkOrderStages.SingleAsync();
        await f.Service.ProcessAsync(new(Guid.NewGuid(),order,stage.Id,f.Shift.Id,false,5,5,0,0,null,f.OperatorPin));
        var operation=Guid.NewGuid();var command=new ReceiveProductionToWarehouseCommand(operation,order,stage.Id,5,f.Location.Id,f.OperatorPin);
        var first=await f.Service.ReceiveWarehouseAsync(command);var retry=await f.Service.ReceiveWarehouseAsync(command);
        Assert.Equal(first.MovementId,retry.MovementId);
        var movement=await f.Db.InventoryMovements.SingleAsync();Assert.Equal(InventoryMovementPurpose.ProductionReceipt,movement.Purpose);
        Assert.Equal(movement.Id,await f.Db.ProductionEvents.Where(x=>x.Type==ProductionEventType.WarehouseReceived).Select(x=>x.InventoryMovementId).SingleAsync());
        Assert.Equal(5,await f.Db.InventoryBalances.SumAsync(x=>x.Quantity));
    }

    [Fact]
    public async Task Production_events_are_immutable()
    {
        await using var f=await Fixture.CreateAsync(1);var order=await f.CreateAndReleaseAsync(2);
        var evt=await f.Db.ProductionEvents.FirstAsync(x=>x.WorkOrderId==order);evt.Reason="Alterado";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Db.SaveChangesAsync());
    }

    private sealed class Fixture:IAsyncDisposable
    {
        private const string Key="AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";public string AdminPin{get;}public string OperatorPin{get;}
        public WarehouseDbContext Db{get;}public ProductionService Service{get;}public Product Product{get;private set;}=null!;public ProductionShift Shift{get;private set;}=null!;public Location Location{get;private set;}=null!;
        private Fixture(WarehouseDbContext db,ProductionService service){Db=db;Service=service;AdminPin="4826";OperatorPin="5937";}
        public static async Task<Fixture>CreateAsync(int stageCount){var db=new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);await db.Database.EnsureCreatedAsync();var pins=new UserPinService(db,new PinProtector(Key));var admin=new User{FullName="Admin producción",RoleId=1,PinLookup="",PinHash=""};var op=new User{FullName="Operador producción",RoleId=2,PinLookup="",PinHash=""};await pins.AssignAsync(admin,"4826");await pins.AssignAsync(op,"5937");db.Users.AddRange(admin,op);var service=new ProductionService(db,pins,new InventoryMovementService(db,pins,TimeProvider.System),TimeProvider.System);var f=new Fixture(db,service);f.Product=new Product{Sku="FG-PROD",BaseUnitId=1};var material=new Product{Sku="MP-PROD",BaseUnitId=1};f.Shift=new ProductionShift{Code="T1",Name="Turno 1"};f.Location=new Location{Code="PT-1",Kind=LocationKind.Area};var wip=new Location{Code="WIP-PROD",Kind=LocationKind.Area,OperationalRole=LocationOperationalRole.Wip};db.AddRange(f.Product,material,f.Shift,f.Location,wip);var stages=Enumerable.Range(1,stageCount).Select(i=>new ProductionStage{Code=$"E{i}",Name=$"Etapa {i}"}).ToArray();stages[0].WipTargets.Add(new ProductionProcessWipTarget{Location=wip});db.AddRange(stages);await db.SaveChangesAsync();var route=await service.CreateRouteAsync(new(Guid.NewGuid(),f.Product.Id,"Ruta prueba",stages.Select(x=>x.Id).ToArray(),f.AdminPin));Assert.Equal(ProductionCommandStatus.Success,route.Status);db.ProductionRecipes.Add(new ProductionRecipe{ProductId=f.Product.Id,Version=1,BaseQuantity=1,Reason="Receta prueba",CreatedByUserId=admin.Id,Lines={new ProductionRecipeLine{MaterialProductId=material.Id,StageId=stages[0].Id,Quantity=1}}});await db.SaveChangesAsync();return f;}
        public async Task<Guid>CreateAndReleaseAsync(decimal quantity){var created=await Service.CreateOrderAsync(new(Guid.NewGuid(),Product.Id,quantity,null,null,null,AdminPin));Assert.Equal(ProductionCommandStatus.Success,created.Status);var order=await Db.ProductionWorkOrders.SingleAsync(x=>x.Id==created.WorkOrderId);order.UsesBatchTraceability=false;await Db.SaveChangesAsync();var released=await Service.ReleaseAsync(new(Guid.NewGuid(),created.WorkOrderId!.Value,null,AdminPin));Assert.True(released.Status==ProductionCommandStatus.Success,string.Join(" | ",released.ValidationErrors));return created.WorkOrderId.Value;}
        public ValueTask DisposeAsync()=>Db.DisposeAsync();
    }
}
