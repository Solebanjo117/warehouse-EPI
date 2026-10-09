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
        var service = new DailyDashboardService(db, new WarehouseSettingsService(db));
        var activity = await service.GetActivityAsync(DateTimeOffset.UtcNow, 90);
        Assert.Equal(90, activity.Points.Count);
        Assert.Equal(snapshot.Metrics.RecentActivityTrend[^1], activity.Points[^1]);
        var products = await service.GetActivityProductsAsync(DateTimeOffset.UtcNow, 90);
        var product = products.Items.SingleOrDefault(item => item.Sku == sku);
        // The shared fixture can contain enough activity to place this SKU on a later page.
        for (var pageNumber = 2; product is null && pageNumber <= products.TotalPages; pageNumber++)
        {
            products = await service.GetActivityProductsAsync(DateTimeOffset.UtcNow, 90, pageNumber);
            product = products.Items.SingleOrDefault(item => item.Sku == sku);
        }
        Assert.NotNull(product);
        Assert.Equal(1, product.Operations);
        Assert.Contains(product.Locations, item => item.Code == $"PGD-{suffix}" && item.Operations == 1);
    }
}
