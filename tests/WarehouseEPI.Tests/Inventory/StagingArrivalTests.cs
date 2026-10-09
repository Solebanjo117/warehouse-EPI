using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Tests.Inventory;

public sealed partial class PalletTrackingTests
{
    [Fact]
    public async Task Staging_counter_includes_unidentified_arrivals_across_batches_and_products()
    {
        await using var f = await Fixture.Create();
        f.Source.Code = "STAGING";
        var other = new Product { Sku = "SECOND-COUNT", BaseUnitId = 1 };
        f.Db.Products.Add(other);
        await f.Db.SaveChangesAsync();
        var query = new StagingArrivalQuery(f.Db);
        Assert.Equal(0, await query.CountPendingAsync());
        for (var i = 0; i < 103; i++)
            f.Db.InventoryMovements.Add(new InventoryMovement { OperationId = Guid.NewGuid(), RequestFingerprint = $"history-{i}",
                Type = InventoryMovementType.Entry, ResponsibleUserId = f.User.Id,
                Lines = [new InventoryMovementLine { ProductId = i % 2 == 0 ? f.Product.Id : other.Id,
                    UnitId = 1, DestinationLocationId = f.Source.Id, Quantity = 1, LineNumber = 1 }] });
        await f.Db.SaveChangesAsync();
        Assert.Equal(103, await query.CountPendingAsync());
        var listed = 0;
        for (var page = 1; ; page++)
        {
            var rows = await query.ListAsync(null, page);
            listed += rows.Items.Count;
            Assert.All(rows.Items, row => Assert.True(row.NeedsIdentification));
            if (!rows.HasMore) break;
        }
        Assert.Equal(listed, await query.CountPendingAsync());
    }

    [Fact]
    public async Task Staging_putaway_moves_selected_arrival_and_retry_does_not_take_another()
    {
        await using var f = await Fixture.Create();
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        var second = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 50, quantities: [20, 30]));
        var query = new StagingArrivalQuery(f.Db);
        var rows = (await query.ListAsync(null)).Items;
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, await query.CountPendingAsync());
        var arrival = rows.Single(r => r.MovementId == second.MovementId);
        Assert.Equal(50, arrival.Pending);
        var request = new StagingPutawayCommand(Guid.NewGuid(), arrival.LineId, f.Destination.Id, arrival.Version, "2468");
        var result = await f.Service.ConfirmStagingAsync(request);
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        var retry = await f.Service.ConfirmStagingAsync(request);
        Assert.Equal(result.MovementId, retry.MovementId);
        Assert.Equal(100, Assert.Single((await query.ListAsync(null)).Items).Pending);
        Assert.Equal(1, await query.CountPendingAsync());
        Assert.Equal(50, await f.Db.InventoryBalances.Where(b => b.LocationId == f.Destination.Id).SumAsync(b => b.Quantity));
        var other = new Location { Code = "OTHER-DEST", Kind = LocationKind.Area };
        f.Db.Locations.Add(other); await f.Db.SaveChangesAsync();
        Assert.Equal(InventoryMovementStatus.IdempotencyConflict,
            (await f.Service.ConfirmStagingAsync(request with { DestinationId = other.Id })).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staging_transfer_is_an_arrival_and_return_is_a_new_arrival(bool identified)
    {
        await using var f = await Fixture.Create();
        f.Destination.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var entryCommand = f.Command(InventoryMovementType.Entry, 100);
        await f.Service.ConfirmAsync(entryCommand with { Lines = [entryCommand.Lines[0] with { PalletQuantities = identified ? [100] : null }] });
        var transfer = f.Command(InventoryMovementType.Transfer, 60);
        var inbound = await f.Service.ConfirmAsync(transfer with { Lines = [transfer.Lines[0] with { AutomaticPalletHandling = true }] });
        Assert.Equal(InventoryMovementStatus.Success, inbound.Status);
        var query = new StagingArrivalQuery(f.Db);
        var arrival = Assert.Single((await query.ListAsync(null)).Items);
        Assert.Equal(inbound.MovementId, arrival.MovementId);
        Assert.Equal(60, arrival.Pending);
        var putaway = await f.Service.ConfirmStagingAsync(new(Guid.NewGuid(), arrival.LineId, f.Source.Id, arrival.Version, "2468"));
        Assert.Equal(InventoryMovementStatus.Success, putaway.Status);
        Assert.Empty((await query.ListAsync(null)).Items);
        Assert.Equal(0, await query.CountPendingAsync());
        var returnCommand = f.Command(InventoryMovementType.Transfer, 60, plates: StagingArrivalQuery.DecodeVersion(
            StagingArrivalQuery.EncodeVersion(putaway.Plates!.Select(p => new PalletSelection(p.PlateId, p.Quantity, p.Version)))));
        var returned = await f.Service.ConfirmAsync(returnCommand);
        Assert.Equal(InventoryMovementStatus.Success, returned.Status);
        arrival = Assert.Single((await query.ListAsync(null)).Items);
        Assert.Equal(returned.MovementId, arrival.MovementId);
        Assert.Equal(1, await query.CountPendingAsync());
        Assert.Equal(60, arrival.Pending);
    }

    [Fact]
    public async Task Staging_stale_partial_or_forged_snapshot_cannot_move_material()
    {
        await using var f = await Fixture.Create();
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        var query = new StagingArrivalQuery(f.Db);
        var arrival = Assert.Single((await query.ListAsync(null)).Items);
        var request = new StagingPutawayCommand(Guid.NewGuid(), arrival.LineId, f.Destination.Id, arrival.Version, "2468");
        Assert.Equal(InventoryMovementStatus.InvalidPin, (await f.Service.ConfirmStagingAsync(request with { Pin = "0000" })).Status);
        var plate = Assert.Single(entry.Plates!);
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Exit, 20, plates: [new(plate.PlateId, 20, plate.Version)]));
        Assert.Equal(InventoryMovementStatus.BalanceChanged, (await f.Service.ConfirmStagingAsync(request)).Status);
        var current = Assert.Single((await query.ListAsync(null)).Items);
        Assert.Equal(80, current.Pending);
        Assert.Equal(1, await query.CountPendingAsync());
        var selections = StagingArrivalQuery.DecodeVersion(current.Version)!;
        var forged = StagingArrivalQuery.EncodeVersion(selections.Select(p => p with { Quantity = 10 }));
        Assert.Equal(InventoryMovementStatus.BalanceChanged, (await f.Service.ConfirmStagingAsync(request with { Version = forged })).Status);
        Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmStagingAsync(request with { Version = current.Version })).Status);
        Assert.Equal(0, await query.CountPendingAsync());
    }

    [Fact]
    public async Task Staging_historical_transfer_requires_physical_identification()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 70);
        await f.Service.ConfirmAsync(entry with { Lines = [entry.Lines[0] with { PalletQuantities = null }] });
        var transfer = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, 70));
        f.Destination.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var query = new StagingArrivalQuery(f.Db);
        var arrival = Assert.Single((await query.ListAsync(null)).Items);
        Assert.True(arrival.NeedsIdentification);
        Assert.Null(arrival.Pending);
        var result = await f.Tracking.IdentifyStagingEntryAsync(new(Guid.NewGuid(), transfer.MovementId!.Value,
            f.Destination.Id, 70, arrival.LineId));
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        arrival = Assert.Single((await query.ListAsync(null)).Items);
        Assert.False(arrival.NeedsIdentification);
        Assert.Equal(70, arrival.Pending);
        Assert.Equal(70, await f.Db.InventoryBalances.SumAsync(b => b.Quantity));
    }

    [Fact]
    public async Task Staging_pagination_filters_exhausted_arrivals_before_paging()
    {
        await using var f = await Fixture.Create();
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var query = new StagingArrivalQuery(f.Db);
        for (var i = 0; i < 28; i++) await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 1));
        var first = (await query.ListAsync(null)).Items[0];
        await f.Service.ConfirmStagingAsync(new(Guid.NewGuid(), first.LineId, f.Destination.Id, first.Version, "2468"));
        var page = await query.ListAsync("PLATE");
        Assert.Equal(25, page.Items.Count); Assert.True(page.HasMore);
        Assert.Equal(2, (await query.ListAsync("PLATE", 2)).Items.Count);
        Assert.Equal(27, await query.CountPendingAsync());
        Assert.Empty((await query.ListAsync("missing")).Items);
    }

    [Fact]
    public async Task Staging_partly_identified_history_recovery_preserves_known_material_and_other_arrivals()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 100);
        await f.Service.ConfirmAsync(entry with { Lines = [entry.Lines[0] with { PalletQuantities = [40, 60] }] });
        var sourcePlate = await f.Db.PalletPlates.SingleAsync(p => p.Quantity == 40);
        // A historical transfer had 40 identified and 60 unplated units.
        var otherPlate = await f.Db.PalletPlates.SingleAsync(p => p.Quantity == 60);
        f.Db.PalletPlateLots.RemoveRange(await f.Db.PalletPlateLots.Where(l => l.PlateId == otherPlate.Id).ToListAsync());
        otherPlate.Quantity = 0; otherPlate.IsVoided = true; await f.Db.SaveChangesAsync();
        var transfer = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, 100,
            plates: [new(sourcePlate.Id, 40, sourcePlate.Version)]));
        f.Destination.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var arrival = Assert.Single((await new StagingArrivalQuery(f.Db).ListAsync(null)).Items);
        Assert.True(arrival.NeedsIdentification);
        var result = await f.Tracking.IdentifyStagingEntryAsync(new(Guid.NewGuid(), transfer.MovementId!.Value,
            f.Destination.Id, 100, arrival.LineId));
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        arrival = Assert.Single((await new StagingArrivalQuery(f.Db).ListAsync(null)).Items);
        Assert.False(arrival.NeedsIdentification); Assert.Equal(100, arrival.Pending);
        Assert.Equal(100, await f.Db.PalletPlates.Where(p => p.LocationId == f.Destination.Id).SumAsync(p => p.Quantity));
    }

    [Fact]
    public async Task Staging_putaway_retains_destination_rules_and_sharing_confirmation()
    {
        await using var f = await Fixture.Create();
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        var arrival = Assert.Single((await new StagingArrivalQuery(f.Db).ListAsync(null)).Items);
        var request = new StagingPutawayCommand(Guid.NewGuid(), arrival.LineId, f.Destination.Id, arrival.Version, "2468");
        f.Destination.OperationalRole = LocationOperationalRole.Wip; await f.Db.SaveChangesAsync();
        Assert.Equal(InventoryMovementStatus.ValidationFailed, (await f.Service.ConfirmStagingAsync(request)).Status);
        (await f.Db.Locations.SingleAsync(l => l.Id == f.Destination.Id)).OperationalRole = LocationOperationalRole.Storage;
        var other = new Product { Sku = "OTHER-STAGING", BaseUnitId = 1 };
        f.Db.Products.Add(other); await f.Db.SaveChangesAsync();
        await f.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "2468", [new(other.Id, 1, DestinationLocationId: f.Destination.Id)]));
        Assert.Equal(InventoryMovementStatus.RequiresLocationSharingConfirmation, (await f.Service.ConfirmStagingAsync(request)).Status);
        Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmStagingAsync(request with { ApprovedLocations = [f.Destination.Id] })).Status);
    }

    [Fact]
    public async Task Staging_corrected_arrival_and_reserved_material_cannot_be_put_away()
    {
        await using var f = await Fixture.Create();
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 50));
        var query = new StagingArrivalQuery(f.Db);
        var row = Assert.Single((await query.ListAsync(null)).Items);
        var requestLine = new ProductionSupplyRequestLine { ProductId = f.Product.Id, UnitId = 1,
            RequiredQuantity = 50, SupplyRequestId = Guid.NewGuid(), MaterialPlanId = Guid.NewGuid() };
        f.Db.ProductionWarehouseReservations.Add(new() { SupplyRequestLine = requestLine, LocationId = f.Source.Id, Quantity = 50 });
        await f.Db.SaveChangesAsync();
        var request = new StagingPutawayCommand(Guid.NewGuid(), row.LineId, f.Destination.Id, row.Version, "2468");
        Assert.Equal(InventoryMovementStatus.ValidationFailed, (await f.Service.ConfirmStagingAsync(request)).Status);
        Assert.Equal(50, (await query.GetAsync(row.LineId))!.Pending);
        f.Db.InventoryMovementCorrections.Add(new() { OriginalMovementId = entry.MovementId!.Value,
            ReversalMovementId = Guid.NewGuid(), RequestFingerprint = "staging-correction", Reason = "Corregida" });
        await f.Db.SaveChangesAsync();
        Assert.Empty((await query.ListAsync(null)).Items);
        Assert.Equal(InventoryMovementStatus.BalanceChanged, (await f.Service.ConfirmStagingAsync(request)).Status);
        Assert.Equal(0, await query.CountPendingAsync());
    }

    [Fact]
    public async Task Staging_historical_merges_are_ambiguous_but_merging_after_putaway_does_not_reopen_arrivals()
    {
        await using var f = await Fixture.Create();
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        foreach (var quantity in new[] { 40m, 60m })
        {
            var command = f.Command(InventoryMovementType.Transfer, quantity);
            await f.Service.ConfirmAsync(command with { Lines = [command.Lines[0] with { AutomaticPalletHandling = true }] });
        }
        f.Destination.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var query = new StagingArrivalQuery(f.Db);
        var rows = (await query.ListAsync(null)).Items;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.NeedsIdentification));
        Assert.Equal(2, await query.CountPendingAsync());
        var first = rows.Single(row => row.Received == 40);
        Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.IdentifyStagingEntryAsync(
            new(Guid.NewGuid(), first.MovementId, first.LocationId, 40, first.LineId))).Status);
        first = (await query.GetAsync(first.LineId))!;
        Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmStagingAsync(
            new(Guid.NewGuid(), first.LineId, f.Source.Id, first.Version, "2468"))).Status);
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 10));
        await f.Tracking.ConsolidateAsync(new(Guid.NewGuid(), f.Product.Id, f.Source.Id));
        Assert.DoesNotContain((await query.ListAsync(null)).Items, row => row.LineId == first.LineId);
    }
}
