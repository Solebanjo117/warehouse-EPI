using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Reporting;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class ProductionReportPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Orders_projection_filters_and_aggregates_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-PROD-REPORT-{suffix}", $"PPR-{suffix}", "5842");
        await using var db = fixture.CreateDbContext();
        var user = await db.Users.SingleAsync(x => x.FullName == $"Operador PG-PROD-REPORT-{suffix}");
        var order = new ProductionWorkOrder
        {
            CreateOperationId = Guid.NewGuid(),
            CreateFingerprint = $"p6-{suffix}",
            Number = $"ORD-P6-{suffix}",
            ProductId = seed.ProductId,
            UnitId = 1,
            OriginalTargetQuantity = 10,
            TargetQuantity = 10,
            AuthorizedQuantity = 10,
            DueDate = new DateOnly(2026, 9, 15),
            Status = ProductionWorkOrderStatus.InProgress,
            CreatedByUserId = user.Id,
            CreatedAt = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero)
        };
        order.Events.Add(new ProductionEvent
        {
            OperationId = Guid.NewGuid(),
            RequestFingerprint = $"receipt-{suffix}",
            Type = ProductionEventType.WarehouseReceived,
            ResponsibleUserId = user.Id,
            Quantity = 4,
            RecordedAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)
        });
        db.ProductionWorkOrders.Add(order);
        await db.SaveChangesAsync();

        var service = new ProductionReportService(db, new WarehouseSettingsService(db),
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero)));
        var result = await service.GetAsync("orders", new ProductionReportFilter(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero), Search: suffix), "Periodo");

        var row = Assert.Single(result.Orders);
        Assert.Equal(4, row.Received);
        Assert.True(row.IsOverdue);

        var baseFilter = new ProductionReportFilter(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero), Search: suffix);
        _ = await service.GetAsync("materials", baseFilter, "Periodo");
        _ = await service.GetAsync("rework", baseFilter, "Periodo");
        var records = await service.GetAsync("records", baseFilter, "Periodo");
        Assert.Contains(records.Records, x => x.Type == nameof(ProductionEventType.WarehouseReceived));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
