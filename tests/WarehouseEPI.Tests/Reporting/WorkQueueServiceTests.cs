using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class WorkQueueServiceTests
{
    [Fact]
    public async Task Queue_orders_open_work_and_limits_each_preview()
    {
        await using var db = CreateDbContext();
        var product = CreateProduct("SKU-QUEUE");
        var user = CreateUser("Admin Cola");
        var location = new Location { Code = "A-1-1", Description = "Rack urgente", Kind = LocationKind.Rack };
        db.AddRange(product, user, location);

        for (var index = 0; index < 9; index++)
        {
            db.ProductionWorkOrders.Add(new ProductionWorkOrder
            {
                CreateOperationId = Guid.NewGuid(),
                CreateFingerprint = $"queue-{index}",
                Number = $"OP-{index:D3}",
                Product = product,
                UnitId = 1,
                TargetQuantity = 10,
                Status = index == 0 ? ProductionWorkOrderStatus.Paused : ProductionWorkOrderStatus.Released,
                DueDate = index == 0 ? new DateOnly(2026, 9, 9) : null,
                CreatedByUser = user,
                CreatedAt = new DateTimeOffset(2026, 9, 1 + index, 12, 0, 0, TimeSpan.Zero)
            });
        }

        var campaign = new CycleCountCampaign
        {
            OperationId = Guid.NewGuid(), Number = 17, Title = "Fila A", Status = CycleCountCampaignStatus.InProgress,
            CreatedByUser = user
        };
        campaign.Locations.Add(new CycleCountLocation { Location = location, SortOrder = 2, Status = CycleCountLocationStatus.Pending });
        campaign.Locations.Add(new CycleCountLocation { Location = new Location { Code = "A-1-2", Kind = LocationKind.Rack }, SortOrder = 1, Status = CycleCountLocationStatus.Stale });
        db.Add(campaign);
        await db.SaveChangesAsync();

        var result = await CreateService(db).GetSnapshotAsync(new WorkQueueFilter(), includeAdmin: false);

        Assert.Equal(9, result.Production.TotalCount);
        Assert.Equal(8, result.Production.Items.Count);
        Assert.True(result.Production.HasMore);
        Assert.Equal("OP-000", result.Production.Items[0].Number);
        Assert.True(result.Production.Items[0].IsOverdue);
        Assert.Equal(CycleCountLocationStatus.Stale, result.CycleCounts.Items[0].Status);
        Assert.Null(result.Exceptions);
    }

    [Fact]
    public async Task Queue_searches_each_source_and_admin_controls_sensitive_sections()
    {
        await using var db = CreateDbContext();
        var product = CreateProduct("SKU-FIND");
        var user = CreateUser("Admin Excepción");
        var location = new Location { Code = "B-2-3", Description = "Zona búsqueda", Kind = LocationKind.Rack };
        db.AddRange(product, user, location);
        db.ProductionWorkOrders.Add(new ProductionWorkOrder
        {
            CreateOperationId = Guid.NewGuid(), CreateFingerprint = "find-order", Number = "OP-FIND",
            ExternalReference = "REF-ABC", Product = product, UnitId = 1, TargetQuantity = 20,
            Status = ProductionWorkOrderStatus.InProgress, CreatedByUser = user
        });
        var campaign = new CycleCountCampaign
        {
            OperationId = Guid.NewGuid(), Number = 21, Title = "Inventario especial",
            Status = CycleCountCampaignStatus.Released, CreatedByUser = user
        };
        campaign.Locations.Add(new CycleCountLocation { Location = location, Status = CycleCountLocationStatus.UnderReview });
        db.Add(campaign);
        db.OperationalExceptionCases.Add(new OperationalExceptionCase
        {
            Category = OperationalExceptionCategory.NegativeInventory,
            Severity = OperationalExceptionSeverity.Critical,
            ConditionKey = "queue-find",
            Status = OperationalExceptionStatus.New,
            PrimaryText = "SKU-FIND",
            SecondaryText = "B-2-3",
            ReasonText = "Saldo negativo",
            TargetUrl = "/Reports/Inventory"
        });
        await db.SaveChangesAsync();

        var publicResult = await CreateService(db).GetSnapshotAsync(new WorkQueueFilter("B-2-3"), includeAdmin: false);
        Assert.Empty(publicResult.CycleCounts.Items);
        Assert.Null(publicResult.Exceptions);

        var adminResult = await CreateService(db).GetSnapshotAsync(new WorkQueueFilter("B-2-3"), includeAdmin: true);
        Assert.Single(adminResult.CycleCounts.Items);
        Assert.Equal("Revisar diferencia", adminResult.CycleCounts.Items[0].ActionLabel);
        Assert.Single(adminResult.Exceptions!.Items);

        var productionResult = await CreateService(db).GetSnapshotAsync(new WorkQueueFilter("REF-ABC"), includeAdmin: false);
        Assert.Single(productionResult.Production.Items);
        Assert.Equal("Continuar", productionResult.Production.Items[0].ActionLabel);
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new WarehouseDbContext(options);
        db.BusinessSettings.Add(new BusinessSettings
        {
            BusinessName = "EPI", WarehouseName = "Central", WarehouseCode = "WH-01", TimeZoneId = "UTC"
        });
        db.Roles.Add(new Role { Id = 2, Code = "ADMIN", Name = "ADMIN" });
        db.Units.Add(new Unit { Id = 1, Code = "EA", Name = "Pieza" });
        db.SaveChanges();
        return db;
    }

    private static WorkQueueService CreateService(WarehouseDbContext db) =>
        new(db, new WarehouseSettingsService(db), new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.Zero)));

    private static Product CreateProduct(string sku) => new()
    {
        Sku = sku, Description = $"Producto {sku}", BaseUnitId = 1, IsActive = true
    };

    private static User CreateUser(string name) => new()
    {
        FullName = name, RoleId = 2, PinLookup = Guid.NewGuid().ToString("N"), PinHash = "hash"
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
