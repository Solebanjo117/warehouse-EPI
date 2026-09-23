using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Inventory;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class PalletTrackingPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Concurrent_identifications_create_only_one_plate_from_the_same_expected_state()
    {
        var seed = await fixture.SeedAsync("PG-IDENTIFY-CONCURRENCY", "PG-IDENTIFY-A", "7314");
        await fixture.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 100, DestinationLocationId: seed.LocationId)]));
        await using var snapshotDb = fixture.CreateDbContext();
        var snapshotService = new PalletTrackingService(snapshotDb,
            new UserPinService(snapshotDb, new PinProtector(PostgreSqlInventoryFixture.LookupKey)), TimeProvider.System);
        var summary = Assert.Single(await snapshotService.IdentificationProductsAsync(seed.LocationId));
        async Task<InventoryMovementResult> IdentifyAsync()
        {
            await using var db = fixture.CreateDbContext();
            var service = new PalletTrackingService(db,
                new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey)), TimeProvider.System);
            return await service.IdentifyAsync(new(Guid.NewGuid(), seed.LocationId, seed.ProductId, 60,
                summary.BalanceVersion, summary.Identifiable));
        }
        var results = await Task.WhenAll(IdentifyAsync(), IdentifyAsync());
        Assert.Single(results, x => x.Status == InventoryMovementStatus.Success);
        Assert.Single(results, x => x.Status == InventoryMovementStatus.BalanceChanged);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(60, await db.PalletPlates.Where(x => x.ProductId == seed.ProductId).SumAsync(x => x.Quantity));
        Assert.Equal(100, await db.InventoryBalances.Where(x => x.ProductId == seed.ProductId).SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Concurrent_plate_withdrawals_reject_stale_version_and_preserve_lots()
    {
        var seed = await fixture.SeedAsync("PG-PLATE-CONCURRENCY", "PG-PLATE-A", "7312");
        var entry = await fixture.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 100, DestinationLocationId: seed.LocationId, PalletQuantities: [100])]));
        var plate = Assert.Single(entry.Plates!);
        var commands = Enumerable.Range(0, 2).Select(_ => new InventoryMovementCommand(Guid.NewGuid(), InventoryMovementType.Exit, seed.Pin,
            [new(seed.ProductId, 60, SourceLocationId: seed.LocationId, Plates: [new(plate.PlateId, 60, plate.Version)])])).ToArray();
        var results = await Task.WhenAll(commands.Select(fixture.ConfirmAsync));
        Assert.Single(results, x => x.Status == InventoryMovementStatus.Success);
        Assert.Single(results, x => x.Status == InventoryMovementStatus.ValidationFailed);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(40, (await db.PalletPlates.SingleAsync(x => x.Id == plate.PlateId)).Quantity);
        Assert.Equal(40, await db.InventoryBalances.Where(x => x.ProductId == seed.ProductId).SumAsync(x => x.Quantity));
        Assert.Equal(40, await db.PalletPlateLots.Where(x => x.PlateId == plate.PlateId).SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task Plate_retry_is_atomic_and_migration_installs_scannable_template()
    {
        var seed = await fixture.SeedAsync("PG-PLATE-RETRY", "PG-PLATE-B", "7313");
        var command = new InventoryMovementCommand(Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 100, DestinationLocationId: seed.LocationId, PalletQuantities: [40, 60])]);
        var results = await Task.WhenAll(fixture.ConfirmAsync(command), fixture.ConfirmAsync(command));
        Assert.All(results, x => Assert.Equal(InventoryMovementStatus.Success, x.Status));
        Assert.Equal(results[0].MovementId, results[1].MovementId);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(2, await db.PalletPlates.CountAsync(x => x.ProductId == seed.ProductId));
        Assert.Equal(100, await db.InventoryBalances.Where(x => x.ProductId == seed.ProductId).SumAsync(x => x.Quantity));
        Assert.True(await db.LabelTemplates.AnyAsync(x => x.Code == "PLT-TRACKED-PALLET" && x.CurrentPublishedVersionId != null));
    }
}
