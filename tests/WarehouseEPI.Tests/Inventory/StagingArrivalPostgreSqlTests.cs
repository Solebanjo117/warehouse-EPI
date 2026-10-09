using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Inventory;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class StagingArrivalPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_splits_preserve_stock_and_never_duplicate_children(bool sameOperation)
    {
        await fixture.WithIsolatedDatabaseAsync(async isolated =>
        {
            var seed = await isolated.SeedAsync("PG-SPLIT", "STAGING", "7384");
            await using (var setup = isolated.CreateDbContext())
            {
                (await setup.Locations.SingleAsync(l => l.Id == seed.LocationId)).Kind = LocationKind.Area;
                await setup.SaveChangesAsync();
            }
            await isolated.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
                [new(seed.ProductId, 100, DestinationLocationId: seed.LocationId)]));
            await using var snapshot = isolated.CreateDbContext();
            var arrival = Assert.Single((await new StagingArrivalQuery(snapshot).ListAsync(null)).Items);
            var plate = Assert.Single(StagingArrivalQuery.DecodeVersion(arrival.Version)!);
            var request = new StagingPlateSplitCommand(Guid.NewGuid(), arrival.LineId, plate.PlateId, plate.ExpectedVersion,
                arrival.Version, [40, 60], seed.Pin);
            async Task<InventoryMovementResult> SplitAsync(StagingPlateSplitCommand command)
            {
                await using var db = isolated.CreateDbContext();
                return await new PalletTrackingService(db,
                    new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey)), TimeProvider.System).SplitStagingPlateAsync(command);
            }
            var results = await Task.WhenAll(SplitAsync(request), SplitAsync(sameOperation ? request : request with { OperationId = Guid.NewGuid() }));
            if (sameOperation)
            {
                Assert.All(results, r => Assert.Equal(InventoryMovementStatus.Success, r.Status));
                Assert.Equal(results[0].Plates, results[1].Plates);
            }
            else
            {
                Assert.Single(results, r => r.Status == InventoryMovementStatus.Success);
                Assert.Single(results, r => r.Status == InventoryMovementStatus.BalanceChanged);
            }
            await using var verify = isolated.CreateDbContext();
            Assert.Equal(3, await verify.PalletPlates.CountAsync());
            Assert.Equal(100, await verify.PalletPlates.SumAsync(p => p.Quantity));
            Assert.Equal(100, await verify.InventoryBalances.SumAsync(b => b.Quantity));
            Assert.Equal(1, await verify.InventoryMovements.CountAsync());
            Assert.Equal(1, await new StagingArrivalQuery(verify).CountPendingAsync());
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_putaways_are_atomic_and_identical_retries_return_the_receipt(bool sameOperation)
    {
        await fixture.WithIsolatedDatabaseAsync(async isolated =>
        {
            var seed = await isolated.SeedAsync("PG-ARRIVAL", "STAGING", "7384");
            Guid destinationId;
            await using (var setup = isolated.CreateDbContext())
            {
                (await setup.Locations.SingleAsync(l => l.Id == seed.LocationId)).Kind = LocationKind.Area;
                var destination = new Location { Code = "STAGING-DEST", Kind = LocationKind.Area };
                setup.Locations.Add(destination); await setup.SaveChangesAsync(); destinationId = destination.Id;
            }
            await isolated.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
                [new(seed.ProductId, 100, DestinationLocationId: seed.LocationId)]));
            await using var snapshot = isolated.CreateDbContext();
            var arrival = Assert.Single((await new StagingArrivalQuery(snapshot).ListAsync(null)).Items);
            var request = new StagingPutawayCommand(Guid.NewGuid(), arrival.LineId, destinationId, arrival.Version, seed.Pin);
            async Task<InventoryMovementResult> PutAsync(StagingPutawayCommand command)
            {
                await using var db = isolated.CreateDbContext();
                var service = new InventoryMovementService(db,
                    new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey)), TimeProvider.System);
                return await service.ConfirmStagingAsync(command);
            }
            var results = await Task.WhenAll(PutAsync(request), PutAsync(sameOperation ? request : request with { OperationId = Guid.NewGuid() }));
            if (sameOperation)
            {
                Assert.All(results, r => Assert.Equal(InventoryMovementStatus.Success, r.Status));
                Assert.Equal(results[0].MovementId, results[1].MovementId);
            }
            else
            {
                Assert.Single(results, r => r.Status == InventoryMovementStatus.Success);
                Assert.Single(results, r => r.Status == InventoryMovementStatus.BalanceChanged);
            }
            await using var verify = isolated.CreateDbContext();
            Assert.Equal(100, await verify.InventoryBalances.Where(b => b.LocationId == destinationId).SumAsync(b => b.Quantity));
            Assert.Equal(1, await verify.InventoryMovements.CountAsync(m => m.Type == InventoryMovementType.Transfer));
            Assert.Empty((await new StagingArrivalQuery(verify).ListAsync(null)).Items);
        });
    }
}
