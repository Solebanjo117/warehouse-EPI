using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Imports;

namespace WarehouseEPI.Tests.Inventory;

public sealed class WipDocumentTests
{
    [Fact]
    public async Task Issue_use_and_return_discount_warehouse_once_and_preserve_lots()
    {
        await using var db = WipTransferImportTests.Db();
        var (pins, product, source, wip) = await SeedAsync(db);
        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        Assert.Equal(InventoryMovementStatus.Success, (await movements.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "1234",
            [new(product.Id, 100, DestinationLocationId: source.Id)]))).Status);
        var issue = new InventoryMovementCommand(Guid.NewGuid(), InventoryMovementType.Exit, "1234", [new(product.Id, 26, SourceLocationId: source.Id)],
            Purpose: InventoryMovementPurpose.ProductionIssue, OperationalAreaId: wip.Id);
        Assert.Equal(InventoryMovementStatus.Success, (await movements.ConfirmAsync(issue)).Status);
        Assert.Equal(InventoryMovementStatus.Success, (await movements.ConfirmAsync(issue)).Status);
        Assert.Equal(74, await BalanceAsync(db, source.Id));
        Assert.False(await db.InventoryBalances.AnyAsync(x => x.LocationId == wip.Id));
        var document = await db.WipDocuments.Include(x => x.Lots).SingleAsync();
        Assert.Equal(26, document.Quantity);
        Assert.Equal(26, document.Lots.Sum(x => x.Quantity));
        var service = new WipDocumentService(db, pins, TimeProvider.System);
        var use = new WipDocumentCommand(Guid.NewGuid(), product.Id, wip.Id, 20, WipDocumentApplicationKind.Consumption, "1234", DocumentId: document.Id);
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(use)).Status);
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(use)).Status);
        Assert.Equal(74, await BalanceAsync(db, source.Id));
        Assert.Equal(InventoryMovementStatus.ValidationFailed, (await service.ConfirmAsync(use with { OperationId = Guid.NewGuid(), Quantity = 7 })).Status);
        var returned = await service.ConfirmAsync(new(Guid.NewGuid(), product.Id, wip.Id, 6, WipDocumentApplicationKind.WarehouseReturn, "1234", source.Id, document.Id));
        Assert.Equal(InventoryMovementStatus.Success, returned.Status);
        Assert.Equal(80, await BalanceAsync(db, source.Id));
        Assert.False(await db.InventoryBalances.AnyAsync(x => x.LocationId == wip.Id));
        Assert.Equal(document.Lots.Single().LotId, (await db.InventoryMovementLines.Include(x => x.BalanceChanges).SingleAsync(x => x.MovementId == returned.MovementId)).BalanceChanges.Single().LotId);
    }

    [Fact]
    public async Task Reversing_documentary_use_and_return_restores_capacity_and_only_reverses_warehouse_entry()
    {
        await using var db = WipTransferImportTests.Db();
        var (pins, product, source, wip) = await SeedAsync(db);
        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        await movements.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "1234", [new(product.Id, 100, DestinationLocationId: source.Id)]));
        await movements.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Exit, "1234", [new(product.Id, 26, SourceLocationId: source.Id)], Purpose: InventoryMovementPurpose.ProductionIssue, OperationalAreaId: wip.Id));
        var service = new WipDocumentService(db, pins, TimeProvider.System);
        var operation = Guid.NewGuid();
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(new(operation, product.Id, wip.Id, 20, WipDocumentApplicationKind.Consumption, "1234"))).Status);
        var reversal = Guid.NewGuid();
        Assert.Equal(InventoryMovementStatus.Success, (await service.ReverseAsync(reversal, operation, "1234", "Corregir uso")).Status);
        Assert.Equal(InventoryMovementStatus.Success, (await service.ReverseAsync(reversal, operation, "1234", "Corregir uso")).Status);
        Assert.Equal(74, await BalanceAsync(db, source.Id));
        operation = Guid.NewGuid();
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(new(operation, product.Id, wip.Id, 26, WipDocumentApplicationKind.WarehouseReturn, "1234", source.Id))).Status);
        Assert.Equal(100, await BalanceAsync(db, source.Id));
        Assert.Equal(InventoryMovementStatus.Success, (await service.ReverseAsync(Guid.NewGuid(), operation, "1234", "Corregir devolución")).Status);
        Assert.Equal(74, await BalanceAsync(db, source.Id));
        Assert.False(await db.InventoryBalances.AnyAsync(x => x.LocationId == wip.Id));
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(new(Guid.NewGuid(), product.Id, wip.Id, 26, WipDocumentApplicationKind.Scrap, "1234", Notes: "Merma confirmada"))).Status);
    }

    [Fact]
    public async Task Ordinary_movements_assignments_and_reclassification_cannot_create_wip_stock()
    {
        await using var db = WipTransferImportTests.Db();
        var (pins, product, source, wip) = await SeedAsync(db);
        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        foreach (var command in new InventoryMovementCommand[]
        {
            new(Guid.NewGuid(), InventoryMovementType.Entry, "1234", [new(product.Id, 1, DestinationLocationId: wip.Id)]),
            new(Guid.NewGuid(), InventoryMovementType.Transfer, "1234", [new(product.Id, 1, source.Id, wip.Id)]),
            new(Guid.NewGuid(), InventoryMovementType.Adjustment, "1234", [new(product.Id, 1, LocationId: wip.Id, ExpectedBalanceVersion: 0)])
        }) Assert.Equal(InventoryMovementStatus.ValidationFailed, (await movements.ConfirmAsync(command)).Status);
        Assert.Equal(ProductLocationAssignmentResult.LocationDoesNotTrackInventory, await new ProductLocationAssignmentService(db).AssignAsync(product.Id, wip.Id));
        await movements.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "1234", [new(product.Id, 1, DestinationLocationId: source.Id)]));
        Assert.NotNull(await LocationWipRules.ValidateChangeAsync(db, source.Id, source.OperationalRole, LocationOperationalRole.Wip, default));
        Assert.False(wip.TracksInventory);
    }

    [Fact]
    public async Task Cutover_rejects_negative_and_stale_preview_then_converts_without_touching_warehouse()
    {
        await using var db = WipTransferImportTests.Db();
        var (pins, product, source, wip) = await SeedAsync(db);
        db.InventoryBalances.Add(new() { ProductId = product.Id, LocationId = source.Id, Quantity = 74 });
        var old = new InventoryBalance { ProductId = product.Id, LocationId = wip.Id, Quantity = -1 };
        db.InventoryBalances.Add(old); await db.SaveChangesAsync();
        var service = new WipDocumentCutoverService(db, pins, TimeProvider.System);
        Assert.NotEmpty((await service.PreviewAsync()).Errors);
        old.Quantity = 26; await db.SaveChangesAsync();
        var preview = await service.PreviewAsync();
        var command = new WipCutoverCommand(Guid.NewGuid(), preview.Revision, "1234", "Pasar a seguimiento documental");
        old.Quantity = 27; await db.SaveChangesAsync();
        Assert.Equal(InventoryMovementStatus.BalanceChanged, (await service.ConfirmAsync(command)).Status);
        command = command with { Revision = (await service.PreviewAsync()).Revision };
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(command)).Status);
        Assert.Equal(InventoryMovementStatus.Success, (await service.ConfirmAsync(command)).Status);
        Assert.Equal(74, await BalanceAsync(db, source.Id));
        Assert.Equal(0, await BalanceAsync(db, wip.Id));
        Assert.Equal(27, (await db.WipDocuments.SingleAsync()).Quantity);
        Assert.True((await db.WipDocuments.SingleAsync()).IsOpening);
        Assert.Single(await db.WipDocumentCutovers.ToListAsync());
    }

    [Fact]
    public async Task Historical_transfer_cutover_preserves_origin_and_correction_only_restores_warehouse()
    {
        await using var db = WipTransferImportTests.Db();
        var (pins, product, source, wip) = await SeedAsync(db);
        var user = await db.Users.SingleAsync();
        var lot = new ProductLot { ProductId = product.Id, Number = "HISTORICAL", NormalizedNumber = "HISTORICAL", LotDate = new(2026, 9, 1) };
        var original = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = "historical-test",
            Type = InventoryMovementType.Transfer,
            Purpose = InventoryMovementPurpose.ProductionIssue,
            OperationalAreaId = wip.Id,
            ResponsibleUserId = user.Id,
            OccurredAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            RecordedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            Lines = [new InventoryMovementLine { ProductId = product.Id, UnitId = product.BaseUnitId, LineNumber = 1,
                SourceLocationId = source.Id, DestinationLocationId = wip.Id, Quantity = 26,
                BalanceChanges = [new() { LocationId = source.Id, LotId = lot.Id, DeltaQuantity = -26, PreviousQuantity = 100, ResultingQuantity = 74 },
                    new() { LocationId = wip.Id, LotId = lot.Id, DeltaQuantity = 26, PreviousQuantity = 0, ResultingQuantity = 26 }] }]
        };
        db.AddRange(lot, original, new InventoryBalance { ProductId = product.Id, LocationId = source.Id, LotId = lot.Id, Quantity = 74 },
            new InventoryBalance { ProductId = product.Id, LocationId = wip.Id, LotId = lot.Id, Quantity = 26 });
        await db.SaveChangesAsync();
        var cutover = new WipDocumentCutoverService(db, pins, TimeProvider.System);
        var preview = await cutover.PreviewAsync();
        Assert.Empty(preview.Errors);
        Assert.Equal(InventoryMovementStatus.Success, (await cutover.ConfirmAsync(new(Guid.NewGuid(), preview.Revision, "1234", "Corte histórico"))).Status);
        var document = await db.WipDocuments.Include(x => x.Lots).SingleAsync();
        Assert.Equal(original.Lines.Single().Id, document.MovementLineId);
        Assert.Equal(original.OccurredAt, document.OccurredAt);
        Assert.Equal(lot.Id, document.Lots.Single().LotId);
        Assert.False(document.IsOpening);
        Assert.Equal(74, await BalanceAsync(db, source.Id));
        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        var correction = new InventoryCorrectionService(db, pins, movements, TimeProvider.System);
        var command = new InventoryCorrectionCommand(Guid.NewGuid(), original.Id, user.Id, "1234", "Reverso posterior al corte");
        var result = await correction.ConfirmAsync(command);
        Assert.True(result.Status == InventoryCorrectionStatus.Success, string.Join("; ", result.ValidationErrors));
        Assert.Equal(InventoryCorrectionStatus.Success, (await correction.ConfirmAsync(command)).Status);
        Assert.Equal(100, await BalanceAsync(db, source.Id));
        Assert.Equal(0, await BalanceAsync(db, wip.Id));
        Assert.True((await db.WipDocuments.SingleAsync()).IsCancelled);
        Assert.Equal(InventoryMovementType.Transfer, (await db.InventoryMovements.SingleAsync(x => x.Id == original.Id)).Type);
    }

    internal static async Task<(UserPinService Pins, Product Product, Location Source, Location Wip)> SeedAsync(WarehouseDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        var pins = WipTransferImportTests.Pins(db);
        var user = new User { FullName = "Document operator", RoleId = 1, PinHash = "", PinLookup = "" };
        await pins.AssignAsync(user, "1234");
        var product = new Product { Sku = "DOCUMENT-PART", BaseUnitId = 1 };
        var source = WipTransferImportTests.Rack("M-6-4");
        var wip = new Location { Code = "M-6-1", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Wip };
        db.AddRange(user, product, source, wip); await db.SaveChangesAsync();
        return (pins, product, source, wip);
    }

    private static Task<decimal> BalanceAsync(WarehouseDbContext db, Guid locationId) => db.InventoryBalances.Where(x => x.LocationId == locationId).SumAsync(x => x.Quantity);
}
