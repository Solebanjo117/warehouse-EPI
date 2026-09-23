using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Reporting;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class WorkQueuePostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Queue_filters_and_projects_all_sources_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var pin = $"5{Convert.ToInt32(suffix[..4], 16) % 1000:D3}";
        var seed = await fixture.SeedAsync($"PG-QUEUE-{suffix}", $"PQUEUE-{suffix}", pin);
        await using var db = fixture.CreateDbContext();
        var user = await db.Users.SingleAsync(item => item.FullName == $"Operador PG-QUEUE-{suffix}");

        db.ProductionWorkOrders.Add(new ProductionWorkOrder
        {
            CreateOperationId = Guid.NewGuid(), CreateFingerprint = $"pg-queue-order-{suffix}",
            Number = $"OP-{suffix}", ExternalReference = suffix, ProductId = seed.ProductId,
            UnitId = 1, TargetQuantity = 5m, Status = ProductionWorkOrderStatus.Released,
            CreatedByUserId = user.Id
        });
        var campaign = new CycleCountCampaign
        {
            OperationId = Guid.NewGuid(), Number = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Title = suffix,
            Status = CycleCountCampaignStatus.InProgress, CreatedByUserId = user.Id
        };
        campaign.Locations.Add(new CycleCountLocation
        {
            LocationId = seed.LocationId, SortOrder = 1, Status = CycleCountLocationStatus.Stale
        });
        db.CycleCountCampaigns.Add(campaign);
        db.OperationalExceptionCases.Add(new OperationalExceptionCase
        {
            Category = OperationalExceptionCategory.NegativeInventory,
            Severity = OperationalExceptionSeverity.Critical,
            ConditionKey = $"pg-queue-{suffix}", Status = OperationalExceptionStatus.New,
            PrimaryText = $"Caso {suffix}", SecondaryText = suffix, ReasonText = "Prueba PostgreSQL",
            TargetUrl = "/Reports/Inventory"
        });
        await db.SaveChangesAsync();

        var service = new WorkQueueService(db, new WarehouseSettingsService(db));
        var result = await service.GetSnapshotAsync(new WorkQueueFilter(suffix), includeAdmin: true);

        Assert.Contains(result.Production.Items, item => item.Number == $"OP-{suffix}");
        Assert.Contains(result.CycleCounts.Items, item => item.CampaignTitle == suffix && item.Status == CycleCountLocationStatus.Stale);
        Assert.Contains(result.Exceptions!.Items, item => item.PrimaryText == $"Caso {suffix}");
    }
}
