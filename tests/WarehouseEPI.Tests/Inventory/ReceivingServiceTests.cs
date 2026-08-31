using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Inventory;

public sealed class ReceivingServiceTests
{
    [Fact]
    public async Task Exact_multiline_receipt_creates_one_document_entry_and_completes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.AddProductAsync("RCV-ONE"); var second = await fixture.AddProductAsync("RCV-TWO");
        var location = await fixture.AddLocationAsync("RCV-A");
        var opened = await fixture.Receiving.OpenAsync(new(Guid.NewGuid(), ReceivingDocumentType.PurchaseOrder, " oc-100 ", " proveedor ", null, null, fixture.Pin,
            [new(first.Id, 2m), new(second.Id, 3m)]));

        var result = await fixture.Receiving.ConfirmAsync(new(Guid.NewGuid(), opened.DocumentId!.Value, fixture.Pin,
            [new(first.Id, 2m, location.Id, "ROLL-1"), new(second.Id, 3m, location.Id)]));

        Assert.Equal(ReceivingCommandStatus.Success, result.Status);
        Assert.Equal(ReceivingDocumentStatus.Completed, result.DocumentStatus);
        var movement = await fixture.Db.InventoryMovements.Include(item => item.Lines).SingleAsync();
        Assert.Equal(InventoryMovementPurpose.DocumentReceipt, movement.Purpose);
        Assert.Equal(2, movement.Lines.Count);
        Assert.Equal("ROLL-1", await fixture.Db.ReceivingConfirmationLines.Where(item => item.ExternalLotReference != null).Select(item => item.ExternalLotReference).SingleAsync());
        Assert.Equal(5m, await fixture.Db.InventoryBalances.SumAsync(item => item.Quantity));
    }

    [Fact]
    public async Task Overage_requires_explicit_acknowledgement_and_note_without_changing_inventory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("RCV-OVER"); var location = await fixture.AddLocationAsync("RCV-B");
        var opened = await fixture.Receiving.OpenAsync(new(Guid.NewGuid(), ReceivingDocumentType.DeliveryNote, "R-1", "Origen", null, null, fixture.Pin, [new(product.Id, 1m)]));

        var rejected = await fixture.Receiving.ConfirmAsync(new(Guid.NewGuid(), opened.DocumentId!.Value, fixture.Pin, [new(product.Id, 2m, location.Id)]));

        Assert.Equal(ReceivingCommandStatus.RequiresDifferenceAcknowledgement, rejected.Status);
        Assert.Empty(await fixture.Db.InventoryMovements.ToListAsync());
        Assert.Empty(await fixture.Db.InventoryBalances.ToListAsync());
    }

    [Fact]
    public async Task Same_operation_is_idempotent_and_different_content_conflicts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("RCV-IDEM"); var operation = Guid.NewGuid();
        var command = new OpenReceivingDocumentCommand(operation, ReceivingDocumentType.PackingList, "PK-9", "Origen", null, null, fixture.Pin, [new(product.Id, 4m)]);

        var first = await fixture.Receiving.OpenAsync(command);
        var retry = await fixture.Receiving.OpenAsync(command);
        var conflict = await fixture.Receiving.OpenAsync(command with { Number = "PK-10" });

        Assert.Equal(first.DocumentId, retry.DocumentId);
        Assert.Equal(ReceivingCommandStatus.IdempotencyConflict, conflict.Status);
        Assert.Equal(1, await fixture.Db.ReceivingDocuments.CountAsync());
    }

    [Fact]
    public async Task Confirmed_receipt_history_is_immutable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var product = await fixture.AddProductAsync("RCV-IMM"); var location = await fixture.AddLocationAsync("RCV-C");
        var opened = await fixture.Receiving.OpenAsync(new(Guid.NewGuid(), ReceivingDocumentType.Other, "X-1", "Origen", null, null, fixture.Pin, [new(product.Id, 1m)]));
        await fixture.Receiving.ConfirmAsync(new(Guid.NewGuid(), opened.DocumentId!.Value, fixture.Pin, [new(product.Id, 1m, location.Id)]));
        var confirmation = await fixture.Db.ReceivingConfirmations.SingleAsync(); confirmation.DifferenceNotes = "alterado";

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Db.SaveChangesAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        public string Pin { get; } = "4826";
        public WarehouseDbContext Db { get; }
        public ReceivingService Receiving { get; }
        private Fixture(WarehouseDbContext db, ReceivingService receiving) { Db=db; Receiving=receiving; }
        public static async Task<Fixture> CreateAsync()
        {
            var db=new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);await db.Database.EnsureCreatedAsync();
            var pins=new UserPinService(db,new PinProtector(Key));var user=new User{FullName="Operador recepción",RoleId=2,PinLookup="",PinHash=""};Assert.Equal(PinAssignmentResult.Success,await pins.AssignAsync(user,"4826"));db.Users.Add(user);await db.SaveChangesAsync();
            var movement=new InventoryMovementService(db,pins,TimeProvider.System);return new(db,new ReceivingService(db,pins,movement,TimeProvider.System));
        }
        public async Task<Product> AddProductAsync(string sku){var item=new Product{Sku=sku,BaseUnitId=1};Db.Products.Add(item);await Db.SaveChangesAsync();return item;}
        public async Task<Location> AddLocationAsync(string code){var item=new Location{Code=code,Kind=LocationKind.Area};Db.Locations.Add(item);await Db.SaveChangesAsync();return item;}
        public ValueTask DisposeAsync()=>Db.DisposeAsync();
    }
}
