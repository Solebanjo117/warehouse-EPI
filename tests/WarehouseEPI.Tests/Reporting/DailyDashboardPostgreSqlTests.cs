using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Reporting;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class DailyDashboardPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Snapshot_queries_translate_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var sku = $"PG-DASH-{suffix}";
        var seed = await fixture.SeedAsync(sku, $"PGD-{suffix}", "4286");

        await using var db = fixture.CreateDbContext();
        var user = await db.Users.SingleAsync(candidate => candidate.FullName == $"Operador {sku}");
        var movement = new InventoryMovement
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('d', 64),
            Type = InventoryMovementType.Entry,
            ResponsibleUserId = user.Id,
            OccurredAt = DateTimeOffset.UtcNow
        };
        movement.Lines.Add(new InventoryMovementLine
        {
            ProductId = seed.ProductId,
            UnitId = 1,
            DestinationLocationId = seed.LocationId,
            Quantity = 1m,
            LineNumber = 1
        });
        db.InventoryMovements.Add(movement);
        await db.SaveChangesAsync();

        var snapshot = await new DailyDashboardService(db, new WarehouseSettingsService(db))
            .GetSnapshotAsync(DateTimeOffset.UtcNow);

        Assert.Equal(14, snapshot.Metrics.RecentActivityTrend.Count);
        Assert.True(snapshot.Metrics.EffectiveMovementsToday >= 1);
        Assert.True(snapshot.Metrics.RecentActivityTrend[^1].EntryCount >= 1);
    }
}
