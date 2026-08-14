using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Inventory;

public sealed class OperationalInventoryQueryServiceTests
{
    [Fact]
    public async Task Resolves_active_sku_barcode_and_location_and_limits_search_results()
    {
        await using var db = CreateDbContext();
        var product = new Product { Sku = "SCAN-001", Description = "Guante azul", BaseUnitId = 1 };
        product.Barcodes.Add(new ProductBarcode { Barcode = "abc-123", IsPrimary = true });
        var location = new Location { Code = "SCAN-AREA", Description = "Área azul", Kind = LocationKind.Area };
        db.AddRange(product, location);
        for (var index = 0; index < 12; index++)
            db.Products.Add(new Product { Sku = $"SEARCH-{index:00}", Description = "Resultado", BaseUnitId = 1 });
        await db.SaveChangesAsync();
        var service = new OperationalInventoryQueryService(db);

        Assert.Equal(product.Id, (await service.ResolveProductAsync(" scan-001 "))?.Id);
        Assert.Equal(product.Id, (await service.ResolveProductAsync("abc-123"))?.Id);
        Assert.Equal(location.Id, (await service.ResolveLocationAsync(" scan-area "))?.Id);
        Assert.Equal(10, (await service.SearchProductsAsync("resultado")).Count);
    }

    [Fact]
    public async Task Operational_resolution_rejects_inactive_blocked_and_lot_status_is_exposed()
    {
        await using var db = CreateDbContext();
        var inactive = new Product { Sku = "INACTIVE-SCAN", BaseUnitId = 1, IsActive = false };
        var lotProduct = new Product { Sku = "LOT-SCAN", BaseUnitId = 1, TracksLots = true };
        var blocked = new Location
        {
            Code = "BLOCKED-SCAN",
            Kind = LocationKind.Area,
            IsBlocked = true,
            BlockReason = "Prueba"
        };
        db.AddRange(inactive, lotProduct, blocked);
        db.ProductLocationAssignments.Add(new ProductLocationAssignment
        {
            Product = lotProduct,
            Location = blocked
        });
        await db.SaveChangesAsync();
        var service = new OperationalInventoryQueryService(db);

        Assert.Null(await service.ResolveProductAsync(inactive.Sku));
        Assert.True((await service.ResolveProductAsync(lotProduct.Sku))?.TracksLots);
        Assert.Null(await service.ResolveLocationAsync(blocked.Code));
        Assert.NotNull(await service.ResolveLocationAsync(blocked.Code, false));
        Assert.Empty(await service.GetProductLocationsAsync(lotProduct.Id));
        Assert.Empty(await service.GetLocationProductsAsync(blocked.Id));
    }

    [Fact]
    public async Task Missing_balance_snapshot_is_zero_without_creating_a_row()
    {
        await using var db = CreateDbContext();
        var product = new Product { Sku = "NO-BALANCE", BaseUnitId = 1 };
        var location = new Location { Code = "NO-BALANCE-AREA", Kind = LocationKind.Area };
        db.AddRange(product, location);
        await db.SaveChangesAsync();

        var snapshot = await new InventoryQueryService(db).GetBalanceAsync(product.Id, location.Id);

        Assert.False(snapshot.Exists);
        Assert.Equal(0m, snapshot.Quantity);
        Assert.Equal(0u, snapshot.Version);
        Assert.Empty(db.InventoryBalances);
    }

    [Fact]
    public async Task Bidirectional_relationships_merge_active_assignments_and_nonzero_balances()
    {
        await using var db = CreateDbContext();
        var product = new Product { Sku = "REL-MAIN", BaseUnitId = 1 };
        var balanceOnlyProduct = new Product { Sku = "REL-BALANCE", BaseUnitId = 1 };
        var assigned = new Location { Code = "REL-A", Kind = LocationKind.Area };
        var balanceOnly = new Location { Code = "REL-B", Kind = LocationKind.Area };
        var both = new Location { Code = "REL-C", Kind = LocationKind.Area };
        var inactiveOnly = new Location { Code = "REL-D", Kind = LocationKind.Area };
        db.AddRange(product, balanceOnlyProduct, assigned, balanceOnly, both, inactiveOnly);
        db.ProductLocationAssignments.AddRange(
            new ProductLocationAssignment { Product = product, Location = assigned },
            new ProductLocationAssignment { Product = product, Location = both },
            new ProductLocationAssignment { Product = product, Location = inactiveOnly, IsActive = false });
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = product, Location = balanceOnly, Quantity = 4.5m },
            new InventoryBalance { Product = product, Location = both, Quantity = 2m },
            new InventoryBalance { Product = balanceOnlyProduct, Location = both, Quantity = -1m });
        await db.SaveChangesAsync();
        var service = new OperationalInventoryQueryService(db);

        var locations = await service.GetProductLocationsAsync(product.Id);
        Assert.Equal(["REL-A", "REL-B", "REL-C"], locations.Select(item => item.Code));
        Assert.True(locations.Single(item => item.Code == "REL-A").HasActiveAssignment);
        Assert.False(locations.Single(item => item.Code == "REL-A").HasNonZeroBalance);
        Assert.False(locations.Single(item => item.Code == "REL-B").HasActiveAssignment);
        Assert.Equal(4.5m, locations.Single(item => item.Code == "REL-B").Quantity);
        Assert.True(locations.Single(item => item.Code == "REL-C").HasActiveAssignment);
        Assert.True(locations.Single(item => item.Code == "REL-C").HasNonZeroBalance);

        var products = await service.GetLocationProductsAsync(both.Id);
        Assert.Equal(["REL-BALANCE", "REL-MAIN"], products.Select(item => item.Sku));
        Assert.False(products.Single(item => item.Sku == "REL-BALANCE").HasActiveAssignment);
        Assert.Equal(-1m, products.Single(item => item.Sku == "REL-BALANCE").Quantity);
        Assert.True(products.Single(item => item.Sku == "REL-MAIN").HasActiveAssignment);
        Assert.True(products.Single(item => item.Sku == "REL-MAIN").HasNonZeroBalance);
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"OperationalQueries-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
