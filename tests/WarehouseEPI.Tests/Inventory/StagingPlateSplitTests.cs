using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;

namespace WarehouseEPI.Tests.Inventory;

public sealed partial class PalletTrackingTests
{
    [Fact]
    public void Calculated_distribution_respects_unit_precision_and_total_limit_without_overflow()
    {
        Assert.True(PalletDistribution.TryCalculateTotal([10, 20], false, out var total));
        Assert.Equal(30, total);
        Assert.True(PalletDistribution.TryCalculateTotal([0.0001m, 0.0002m], true, out total));
        Assert.Equal(0.0003m, total);
        foreach (var parts in new decimal[][] { [0, 20], [-1, 20], [0.00001m, 20], [decimal.MaxValue, decimal.MaxValue],
            [99_999_999_999_999.9999m, 1], [10], Enumerable.Repeat(1m, 101).ToArray() })
            Assert.False(PalletDistribution.TryCalculateTotal(parts, true, out _));
        Assert.False(PalletDistribution.TryCalculateTotal([1.5m, 2], false, out _));
    }

    [Fact]
    public async Task Split_preserves_multiple_lots_and_supports_print_batches_over_one_hundred()
    {
        await using var f = await Fixture.Create(); f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 200));
        var plate = await f.Db.PalletPlates.Include(p => p.Lots).SingleAsync();
        Assert.Single(plate.Lots).Quantity = 50;
        (await f.Db.InventoryBalances.SingleAsync()).Quantity = 50;
        var extra = new ProductLot { ProductId = f.Product.Id, Number = "LOT-2", NormalizedNumber = "LOT-2" };
        f.Db.ProductLots.Add(extra);
        plate.Lots.Add(new() { PlateId = plate.Id, LotId = extra.Id, Quantity = 150 });
        f.Db.InventoryBalances.Add(new() { ProductId = f.Product.Id, LocationId = f.Source.Id, LotId = extra.Id, Quantity = 150 });
        await f.Db.SaveChangesAsync();
        var query = new StagingArrivalQuery(f.Db);
        var row = Assert.Single((await query.ListAsync(null)).Items);
        var first = await f.Tracking.SplitStagingPlateAsync(new(Guid.NewGuid(), row.LineId, plate.Id, plate.Version, row.Version,
            Enumerable.Repeat(2m, 100).ToArray(), "2468"));
        Assert.Equal(InventoryMovementStatus.Success, first.Status);
        row = (await query.GetAsync(row.LineId))!;
        var child = first.Plates![0];
        Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.SplitStagingPlateAsync(new(Guid.NewGuid(), row.LineId,
            child.PlateId, child.Version, row.Version, [1, 1], "2468"))).Status);
        row = (await query.GetAsync(row.LineId))!;
        var selections = StagingArrivalQuery.DecodeVersion(row.Version)!;
        Assert.Equal(101, selections.Count); Assert.Equal(200, row.Pending);
        foreach (var batch in selections.Chunk(100))
            Assert.True(await f.Tracking.CanPrintStagingArrivalAsync(row.LineId, f.Source.Id, batch.Select(p => p.PlateId).ToArray(), row.Version));
        Assert.False(await f.Tracking.CanPrintStagingArrivalAsync(row.LineId, f.Source.Id, [plate.Id], row.Version));
        Assert.False(await f.Tracking.CanPrintStagingArrivalAsync(row.LineId, f.Source.Id, [Guid.NewGuid()], row.Version));
        Assert.Equal(150, await f.Db.PalletPlateLots.Where(l => l.LotId == extra.Id).SumAsync(l => l.Quantity));
        Assert.Equal(50, await f.Db.PalletPlateLots.Where(l => l.LotId != extra.Id).SumAsync(l => l.Quantity));
        Assert.Equal(200, await f.Db.InventoryBalances.SumAsync(b => b.Quantity));
        Assert.Equal(1, await query.CountPendingAsync());
    }

    [Fact]
    public async Task Split_historical_identified_arrival_keeps_identity()
    {
        await using var f = await Fixture.Create();
        var entry = f.Command(InventoryMovementType.Entry, 70);
        await f.Service.ConfirmAsync(entry with { Lines = [entry.Lines[0] with { PalletQuantities = null }] });
        var transfer = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, 70));
        f.Destination.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var query = new StagingArrivalQuery(f.Db);
        var row = Assert.Single((await query.ListAsync(null)).Items);
        Assert.True(row.NeedsIdentification);
        Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.IdentifyStagingEntryAsync(new(Guid.NewGuid(), transfer.MovementId!.Value,
            f.Destination.Id, 70, row.LineId))).Status);
        row = (await query.GetAsync(row.LineId))!;
        var plate = Assert.Single(StagingArrivalQuery.DecodeVersion(row.Version)!);
        Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.SplitStagingPlateAsync(new(Guid.NewGuid(), row.LineId, plate.PlateId,
            plate.ExpectedVersion, row.Version, [30, 40], "2468"))).Status);
        var after = Assert.Single((await query.ListAsync(null)).Items);
        Assert.Equal(row.LineId, after.LineId); Assert.Equal(70, after.Pending); Assert.False(after.NeedsIdentification);
    }

    [Fact]
    public async Task Split_preserves_arrival_lots_balances_and_retries_and_blocks_original_print_and_correction()
    {
        await using var f = await Fixture.Create();
        f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100, quantities: [70, 30]));
        var query = new StagingArrivalQuery(f.Db);
        var arrival = Assert.Single((await query.ListAsync(null)).Items);
        var plate = entry.Plates!.Single(p => p.Quantity == 70);
        var request = new StagingPlateSplitCommand(Guid.NewGuid(), arrival.LineId, plate.PlateId, plate.Version, arrival.Version, [20, 50], "2468");
        var balances = await f.Db.InventoryBalances.Select(b => new { b.Id, b.Quantity, b.Version }).ToListAsync();
        var lotTotals = await f.Db.PalletPlateLots.GroupBy(l => l.LotId).Select(g => new { Id = g.Key, Quantity = g.Sum(l => l.Quantity) }).ToListAsync();
        var result = await f.Tracking.SplitStagingPlateAsync(request);
        Assert.Equal(InventoryMovementStatus.Success, result.Status);
        Assert.Equal(new[] { 20m, 50m }, result.Plates!.Select(p => p.Quantity).Order());
        Assert.Equal(result.Plates, (await f.Tracking.SplitStagingPlateAsync(request)).Plates);
        Assert.Equal(InventoryMovementStatus.IdempotencyConflict, (await f.Tracking.SplitStagingPlateAsync(request with { Quantities = [30, 40] })).Status);
        Assert.Equal(balances, await f.Db.InventoryBalances.Select(b => new { b.Id, b.Quantity, b.Version }).ToListAsync());
        Assert.Equal(lotTotals, await f.Db.PalletPlateLots.GroupBy(l => l.LotId).Select(g => new { Id = g.Key, Quantity = g.Sum(l => l.Quantity) }).ToListAsync());
        Assert.Equal(1, await f.Db.InventoryMovements.CountAsync());
        Assert.Equal(1, await query.CountPendingAsync());
        arrival = (await query.GetAsync(arrival.LineId))!;
        Assert.False(arrival.NeedsIdentification); Assert.Equal(100, arrival.Pending);
        Assert.Equal(3, StagingArrivalQuery.DecodeVersion(arrival.Version)!.Count);
        Assert.Equal("Dividida", Assert.Single(await f.Tracking.SearchAsync(plate.Identifier)).Status);
        Assert.Equal(PalletLicensePlateStatus.NotEligible, (await new PalletLicensePlateService(f.Db).LoadAsync(plate.PlateId)).Status);
        Assert.Equal(InventoryCorrectionStatus.ValidationFailed,
            (await new InventoryCorrectionService(f.Db, f.Pins, f.Service, TimeProvider.System)
                .ConfirmAsync(new(Guid.NewGuid(), entry.MovementId!.Value, f.User.Id, "2468", "Prueba"))).Status);
        var child = result.Plates!.Single(p => p.Quantity == 50);
        var again = await f.Tracking.SplitStagingPlateAsync(new(Guid.NewGuid(), arrival.LineId, child.PlateId, child.Version, arrival.Version, [25, 25], "2468"));
        Assert.Equal(InventoryMovementStatus.Success, again.Status);
        arrival = (await query.GetAsync(arrival.LineId))!;
        Assert.Equal(100, arrival.Pending); Assert.False(arrival.NeedsIdentification);
        Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmStagingAsync(new(Guid.NewGuid(), arrival.LineId, f.Destination.Id, arrival.Version, "2468"))).Status);
        Assert.Equal(0, await query.CountPendingAsync());
        // An identical retry retains the same child folios even after later use.
        Assert.Equal(result.Plates, (await f.Tracking.SplitStagingPlateAsync(request)).Plates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Split_after_partial_or_return_to_staging_inherits_current_arrival(bool partial)
    {
        await using var f = await Fixture.Create(); f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        var plate = Assert.Single(entry.Plates!);
        var transfer = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Transfer, partial ? 40 : 100,
            plates: [new(plate.PlateId, partial ? 40 : 100, plate.Version)]));
        if (!partial)
        {
            var moved = Assert.Single(transfer.Plates!, p => p.Quantity == 100);
            Assert.Equal(InventoryMovementStatus.Success, (await f.Service.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Transfer, "2468",
                [new(f.Product.Id, 100, SourceLocationId: f.Destination.Id, DestinationLocationId: f.Source.Id, Plates: [new(moved.PlateId, 100, moved.Version)])]))).Status);
        }
        var query = new StagingArrivalQuery(f.Db); var row = Assert.Single((await query.ListAsync(null)).Items);
        var current = StagingArrivalQuery.DecodeVersion(row.Version)!.Single();
        Assert.Equal(InventoryMovementStatus.Success, (await f.Tracking.SplitStagingPlateAsync(new(Guid.NewGuid(), row.LineId,
            current.PlateId, current.ExpectedVersion, row.Version, [20, current.Quantity - 20], "2468"))).Status);
        row = Assert.Single((await query.ListAsync(null)).Items);
        Assert.Equal(partial ? 60 : 100, row.Pending); Assert.False(row.NeedsIdentification);
    }

    [Fact]
    public async Task Split_rejects_bad_distribution_stale_version_and_reserved_lot_without_writes()
    {
        await using var f = await Fixture.Create(); f.Source.Code = "STAGING"; await f.Db.SaveChangesAsync();
        (await f.Db.Units.SingleAsync(u => u.Id == f.Product.BaseUnitId)).AllowsDecimals = false;
        await f.Db.SaveChangesAsync();
        var entry = await f.Service.ConfirmAsync(f.Command(InventoryMovementType.Entry, 100));
        var row = Assert.Single((await new StagingArrivalQuery(f.Db).ListAsync(null)).Items);
        var plate = Assert.Single(entry.Plates!);
        var command = new StagingPlateSplitCommand(Guid.NewGuid(), row.LineId, plate.PlateId, plate.Version, row.Version, [40, 60], "2468");
        foreach (var parts in new decimal[][] { [40, 40], [0, 100], [-1, 101], [100], [0.5m, 99.5m], Enumerable.Repeat(1m, 101).ToArray() })
            Assert.Equal(InventoryMovementStatus.ValidationFailed, (await f.Tracking.SplitStagingPlateAsync(command with { Quantities = parts })).Status);
        Assert.Equal(InventoryMovementStatus.InvalidPin, (await f.Tracking.SplitStagingPlateAsync(command with { Pin = "0000" })).Status);
        Assert.Equal(InventoryMovementStatus.BalanceChanged, (await f.Tracking.SplitStagingPlateAsync(command with { ExpectedVersion = 999 })).Status);
        var lotId = await f.Db.PalletPlateLots.Where(l => l.PlateId == plate.PlateId).Select(l => l.LotId).SingleAsync();
        f.Db.ProductionWarehouseReservations.Add(new() { LocationId = f.Source.Id, LotId = lotId, Quantity = 1,
            SupplyRequestLine = new() { ProductId = f.Product.Id, UnitId = 1, RequiredQuantity = 1, MaterialPlanId = Guid.NewGuid(), SupplyRequestId = Guid.NewGuid() } });
        await f.Db.SaveChangesAsync();
        Assert.Equal(InventoryMovementStatus.ValidationFailed, (await f.Tracking.SplitStagingPlateAsync(command)).Status);
        Assert.Single(await f.Db.PalletPlates.ToListAsync());
        Assert.DoesNotContain(await f.Db.PalletPlateEvents.ToListAsync(), e => e.Kind.StartsWith("StagingSplit", StringComparison.Ordinal));
    }
}
