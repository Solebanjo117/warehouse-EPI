using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Inventory;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class PostgreSqlCycleCountTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Cycle_count_constraints_versions_and_atomic_approval_work_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-COUNT-{suffix}", $"PGC-{suffix}", "4386");
        var entry = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 7m, DestinationLocationId: seed.LocationId)]));
        Assert.Equal(InventoryMovementStatus.Success, entry.Status);

        await using var db = fixture.CreateDbContext();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var movements = new InventoryMovementService(db, pins, TimeProvider.System);
        var service = new CycleCountService(db, pins, new InventoryQueryService(db), movements, TimeProvider.System);

        var created = await service.CreateAsync(new(seed.Pin, "Conteo PostgreSQL", null, [seed.LocationId], OperationId: Guid.NewGuid()));
        Assert.Equal(CycleCountStatus.Success, created.Status);
        Assert.Equal(CycleCountStatus.Success, (await service.ReleaseAsync(created.CampaignId!.Value, Guid.NewGuid(), seed.Pin)).Status);
        var location = Assert.Single((await service.GetCampaignAsync(created.CampaignId.Value))!.Locations);
        var started = await service.StartAttemptAsync(location.Id, Guid.NewGuid(), seed.Pin);
        var submitted = await service.SubmitAsync(new(started.AttemptId!.Value, Guid.NewGuid(), seed.Pin, [new(seed.ProductId, 5m)]));
        var approved = await service.ApproveAsync(new(location.Id, Guid.NewGuid(), seed.Pin, "Validación PostgreSQL"));

        Assert.Equal(CycleCountStatus.Success, submitted.Status);
        Assert.Equal(CycleCountStatus.Success, approved.Status);
        var movement = await db.InventoryMovements.Include(item => item.Lines)
            .SingleAsync(item => item.Id == approved.MovementId);
        Assert.Equal(InventoryMovementPurpose.CycleCountAdjustment, movement.Purpose);
        Assert.Single(movement.Lines);
        Assert.Equal(5m, (await new InventoryQueryService(db).GetBalanceAsync(seed.ProductId, seed.LocationId)).Quantity);
        Assert.Equal(CycleCountCampaignStatus.Completed, (await service.GetCampaignAsync(created.CampaignId.Value))!.Status);
    }
}
