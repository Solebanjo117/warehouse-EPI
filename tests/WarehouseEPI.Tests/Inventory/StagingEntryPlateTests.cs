using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Tests.Inventory;

public sealed partial class PalletTrackingTests
{
    [Fact]
    public async Task Staging_pre_lot_history_uses_current_available_lots_and_keeps_the_original_unchanged()
    {
        await using var f = await Fixture.Create();
        var movement = new InventoryMovement { Type = InventoryMovementType.Entry, ResponsibleUserId = f.User.Id,
            RequestFingerprint = "pre-lot", OperationId = Guid.NewGuid() };
        var line = new InventoryMovementLine { Movement = movement, ProductId = f.Product.Id, UnitId = 1,
            DestinationLocationId = f.Source.Id, Quantity = 50, LineNumber = 1 };
        var lot = new ProductLot { ProductId = f.Product.Id, Number = "MIGRATED", NormalizedNumber = "MIGRATED" };
        f.Db.AddRange(movement, line, lot, new InventoryBalance { ProductId = f.Product.Id,
            LocationId = f.Source.Id, LotId = lot.Id, Quantity = 50 });
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var recovered = await f.Tracking.IdentifyStagingEntryAsync(new(Guid.NewGuid(), movement.Id, f.Source.Id, 50));
        Assert.Equal(InventoryMovementStatus.Success, recovered.Status);
        Assert.Null(line.LotId);
        Assert.All(line.BalanceChanges, x => Assert.Null(x.LotId));
        Assert.Equal(50, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Staging_corrected_entries_are_not_listed_or_recovered()
    {
        await using var f = await Fixture.Create();
        var command = f.Command(InventoryMovementType.Entry, 50);
        var result = await f.Service.ConfirmAsync(command with { Lines = [command.Lines[0] with { PalletQuantities = null }] });
        f.Db.InventoryMovementCorrections.Add(new() { OriginalMovementId = result.MovementId!.Value,
            ReversalMovementId = Guid.NewGuid(), RequestFingerprint = "correction", Reason = "Corregida" });
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        Assert.Empty((await f.Tracking.StagingEntriesAsync(f.Source.Id)).Items);
        Assert.Equal(InventoryMovementStatus.ValidationFailed,
            (await f.Tracking.IdentifyStagingEntryAsync(new(Guid.NewGuid(), result.MovementId.Value, f.Source.Id, 50))).Status);
        Assert.Empty(await f.Db.PalletPlates.ToListAsync());
    }

    [Fact]
    public async Task Staging_reserved_history_cannot_be_reassigned()
    {
        await using var f = await Fixture.Create();
        var command = f.Command(InventoryMovementType.Entry, 50);
        var result = await f.Service.ConfirmAsync(command with { Lines = [command.Lines[0] with { PalletQuantities = null }] });
        var requestLine = new ProductionSupplyRequestLine { ProductId = f.Product.Id, UnitId = 1,
            SupplyRequestId = Guid.NewGuid(), MaterialPlanId = Guid.NewGuid(), RequiredQuantity = 10 };
        f.Db.ProductionWarehouseReservations.Add(new() { SupplyRequestLine = requestLine, LocationId = f.Source.Id,
            LotId = (await f.Db.ProductLots.SingleAsync()).Id, Quantity = 10 });
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        Assert.Equal(InventoryMovementStatus.ValidationFailed,
            (await f.Tracking.IdentifyStagingEntryAsync(new(Guid.NewGuid(), result.MovementId!.Value, f.Source.Id, 50))).Status);
        Assert.Empty(await f.Db.PalletPlates.ToListAsync());
    }

    [Fact]
    public async Task Staging_new_entries_keep_individual_plates_and_reject_consolidation()
    {
        await using var f = await Fixture.Create();
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        foreach (var amount in new[] { 100m, 150m, 80m })
        {
            var command = f.Command(InventoryMovementType.Entry, amount);
            Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmAsync(command with
                { Lines = [command.Lines[0] with { PalletQuantities = null }] })).Status);
        }
        Assert.Equal(new[] { 80m, 100m, 150m }, await f.Db.PalletPlates.OrderBy(x => x.Quantity).Select(x => x.Quantity).ToArrayAsync());
        var page = await f.Tracking.StagingEntriesAsync(f.Source.Id);
        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, row => { Assert.False(row.NeedsIdentification); Assert.Single(row.Plates); });
        Assert.True(await f.Tracking.CanPrintStagingPlatesAsync(f.Source.Id, page.Items.SelectMany(x => x.Plates).Select(x => x.Id).ToArray()));
        Assert.Equal(InventoryMovementStatus.ValidationFailed, (await f.Tracking.ConsolidateAsync(new(Guid.NewGuid(), f.Product.Id, f.Source.Id))).Status);
        Assert.Equal(330, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staging_historical_entries_split_free_or_consolidated_stock_without_changing_balances(bool consolidated)
    {
        await using var f = await Fixture.Create();
        var entryIds = new List<Guid>();
        foreach (var amount in new[] { 100m, 150m })
        {
            var command = f.Command(InventoryMovementType.Entry, amount);
            var result = await f.Service.ConfirmAsync(command with { Lines = [command.Lines[0] with { PalletQuantities = null }] });
            entryIds.Add(result.MovementId!.Value);
        }
        if (consolidated)
        {
            var summary = Assert.Single(await f.Tracking.IdentificationProductsAsync(f.Source.Id));
            Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.IdentifyAsync(new(Guid.NewGuid(), f.Source.Id,
                f.Product.Id, 250, summary.BalanceVersion, summary.Identifiable))).Status);
        }
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        Assert.All((await f.Tracking.StagingEntriesAsync(f.Source.Id)).Items, x => Assert.True(x.NeedsIdentification));
        var command1 = new StagingEntryCommand(Guid.NewGuid(), entryIds[0], f.Source.Id, 100);
        var first = await f.Tracking.IdentifyStagingEntryAsync(command1);
        Assert.Equal(InventoryMovementStatus.Success, first.Status);
        var retry = await f.Tracking.IdentifyStagingEntryAsync(command1);
        Assert.Equal(first.Plates!.Single().PlateId, retry.Plates!.Single().PlateId);
        Assert.Equal(InventoryMovementStatus.IdempotencyConflict,
            (await f.Tracking.IdentifyStagingEntryAsync(command1 with { Quantity = 90 })).Status);
        Assert.Equal(InventoryMovementStatus.ValidationFailed,
            (await f.Tracking.IdentifyStagingEntryAsync(command1 with { OperationId = Guid.NewGuid() })).Status);
        Assert.Equal(InventoryMovementStatus.Success,
            (await f.Tracking.IdentifyStagingEntryAsync(new(Guid.NewGuid(), entryIds[1], f.Source.Id, 150))).Status);
        Assert.Equal(new[] { 100m, 150m }, (await f.Tracking.StagingEntriesAsync(f.Source.Id)).Items
            .SelectMany(x => x.Plates).Select(x => x.Quantity).Order().ToArray());
        Assert.Equal(250, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.Equal(250, await f.Db.PalletPlates.SumAsync(x => x.Quantity));
        Assert.Equal(2, await f.Db.InventoryMovements.CountAsync());
    }

    [Fact]
    public async Task Staging_recovery_does_not_reuse_original_consolidated_plate_as_entry_label()
    {
        await using var f = await Fixture.Create();
        var first = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 150));
        await f.Tracking.ConsolidateAsync(new(Guid.NewGuid(), f.Product.Id, f.Source.Id));
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var result = await f.Tracking.IdentifyStagingEntryAsync(new(Guid.NewGuid(), first.MovementId!.Value, f.Source.Id, 100));
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        var row = (await f.Tracking.StagingEntriesAsync(f.Source.Id)).Items.Single(x => x.MovementId == first.MovementId);
        Assert.Equal(100, Assert.Single(row.Plates).Quantity);
        Assert.False(await f.Tracking.CanPrintStagingPlatesAsync(f.Source.Id, [first.Plates!.Single().PlateId]));
        Assert.Equal(250, await f.Db.PalletPlates.SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Staging_historical_recovery_requires_real_remaining_stock_and_is_area_scoped()
    {
        await using var f = await Fixture.Create();
        var command = f.Command(InventoryMovementType.Entry, 100);
        var entry = await f.Service.ConfirmAsync(command with { Lines = [command.Lines[0] with { PalletQuantities = null }] });
        var request = new StagingEntryCommand(Guid.NewGuid(), entry.MovementId!.Value, f.Source.Id, 100);
        Assert.Equal(InventoryMovementStatus.ValidationFailed, (await f.Tracking.IdentifyStagingEntryAsync(request)).Status);
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 30));
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        Assert.Equal(InventoryMovementStatus.ValidationFailed, (await f.Tracking.IdentifyStagingEntryAsync(request)).Status);
        Assert.Empty(await f.Db.PalletPlates.ToListAsync());
        Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.IdentifyStagingEntryAsync(request with { Quantity = 70 })).Status);
        Assert.Equal(70, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
        Assert.Equal(70, Assert.Single((await f.Tracking.StagingEntriesAsync(f.Source.Id)).Items.Single().Plates).Quantity);
    }

    [Fact]
    public async Task Staging_automatic_adjustment_preserves_entry_identity()
    {
        await using var f = await Fixture.Create();
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 150));
        var balance = await new InventoryQueryService(f.Db).GetBalanceAsync(f.Product.Id, f.Source.Id);
        var result = await f.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Adjustment, "2468",
            [new(f.Product.Id, 260, LocationId: f.Source.Id, ExpectedBalanceVersion: balance.Version, AutomaticPalletHandling: true)], Notes: "Conteo"));
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Equal(new[] { 100m, 150m }, await f.Db.PalletPlates.OrderBy(x => x.Quantity).Select(x => x.Quantity).ToArrayAsync());
        Assert.Equal(260, await f.Db.InventoryBalances.SumAsync(x => x.Quantity));
    }
}
