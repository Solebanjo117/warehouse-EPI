using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class OperationalExceptionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reconciliation_creates_a_case_from_a_derived_condition()
    {
        await using var db = CreateDbContext();
        var owner = User("Administradora");
        var product = new Product { Sku = "EXCEPTION-NEG", BaseUnitId = 1, MinimumStock = -10m };
        var location = new Location { Code = "A-01-01", Kind = LocationKind.Rack };
        var balance = new InventoryBalance { Product = product, Location = location, Quantity = -2m };
        db.AddRange(owner, product, location, balance, new ProductLocationAssignment { Product = product, Location = location });
        await db.SaveChangesAsync();
        var service = Service(db);

        var created = await service.ReconcileAsync();
        var page = await service.GetPageAsync(new(Category: OperationalExceptionCategory.NegativeInventory));
        var item = Assert.Single(page.Items);

        Assert.Equal(1, created.Created);
        Assert.Equal(OperationalExceptionCategory.NegativeInventory, item.Category);
        Assert.Equal(OperationalExceptionStatus.New, item.Status);
        Assert.Contains("/Operations/Adjustment?productId=", item.TargetUrl, StringComparison.Ordinal);

        var detail = await service.GetDetailAsync(item.Id);
        Assert.Contains(detail!.Events, history => history.Type == OperationalExceptionEventType.Detected);
    }

    [Fact]
    public async Task Assignable_users_exclude_inactive_accounts()
    {
        await using var db = CreateDbContext();
        var owner = User("Administradora");
        var inactive = User("Inactivo");
        inactive.IsActive = false;
        var product = new Product { Sku = "EXCEPTION-MIN", BaseUnitId = 1, MinimumStock = 1m };
        db.AddRange(owner, inactive, product);
        await db.SaveChangesAsync();
        var assignees = await Service(db).GetAssignableUsersAsync();
        Assert.Contains(assignees, item => item.Id == owner.Id);
        Assert.DoesNotContain(assignees, item => item.Id == inactive.Id);
    }

    private static OperationalExceptionService Service(WarehouseDbContext db)
    {
        var settings = new WarehouseSettingsService(db);
        return new(db, new OperationalAlertService(db, settings, new WarehouseClock(settings), new FixedTimeProvider(Now)), new FixedTimeProvider(Now));
    }

    private static User User(string name) => new() { FullName = name, PinLookup = Guid.NewGuid().ToString("N"), PinHash = "hash", RoleId = 1 };

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase($"OperationalExceptionTests-{Guid.NewGuid():N}").Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
