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
    public async Task Combined_resolution_preserves_exact_product_location_and_ambiguous_matches()
    {
        await using var db = CreateDbContext();
        var product = new Product { Sku = "CROSS-001", BaseUnitId = 1 };
        product.Barcodes.Add(new ProductBarcode { Barcode = "CROSS-BAR", IsPrimary = true });
        var location = new Location { Code = "CROSS-LOC", Kind = LocationKind.Area };
        var ambiguousLocation = new Location { Code = "CROSS-001", Kind = LocationKind.Area };
        db.AddRange(product, location, ambiguousLocation);
        await db.SaveChangesAsync();
        var service = new OperationalInventoryQueryService(db);

        var barcode = await service.ResolveCodeAsync(" CROSS-BAR ");
        Assert.Equal(product.Id, barcode.Product?.Id);
        Assert.Null(barcode.Location);

        var resolvedLocation = await service.ResolveCodeAsync("cross-loc");
        Assert.Null(resolvedLocation.Product);
        Assert.Equal(location.Id, resolvedLocation.Location?.Id);

        var ambiguous = await service.ResolveCodeAsync("cross-001");
        Assert.Equal(product.Id, ambiguous.Product?.Id);
        Assert.Equal(ambiguousLocation.Id, ambiguous.Location?.Id);

        var missing = await service.ResolveCodeAsync("missing");
        Assert.Null(missing.Product);
        Assert.Null(missing.Location);
    }

    [Fact]
    public async Task Operational_resolution_rejects_inactive_blocked_and_lot_status_is_exposed()
    {
        await using var db = CreateDbContext();
        var inactive = new Product { Sku = "INACTIVE-SCAN", BaseUnitId = 1, IsActive = false };
        var lotProduct = new Product { Sku = "LOT-SCAN", BaseUnitId = 1 };
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
        Assert.NotNull(await service.ResolveProductAsync(lotProduct.Sku));
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

    [Fact]
    public async Task Public_inventory_positions_include_active_assignments_and_nonzero_balances()
    {
        await using var db = CreateDbContext();
        var product = new Product { Sku = "PUBLIC-MAIN", BaseUnitId = 1 };
        var inactiveProduct = new Product { Sku = "PUBLIC-INACTIVE", BaseUnitId = 1, IsActive = false };
        var assigned = new Location { Code = "PUBLIC-A", Kind = LocationKind.Area };
        var both = new Location { Code = "PUBLIC-B", Kind = LocationKind.Area };
        var balanceOnly = new Location { Code = "PUBLIC-C", Kind = LocationKind.Area };
        var inactiveAssignment = new Location { Code = "PUBLIC-D", Kind = LocationKind.Area };
        var blocked = new Location { Code = "PUBLIC-E", Kind = LocationKind.Area, IsBlocked = true, BlockReason = "Prueba" };
        db.AddRange(product, inactiveProduct, assigned, both, balanceOnly, inactiveAssignment, blocked);
        db.ProductLocationAssignments.AddRange(
            new ProductLocationAssignment { Product = product, Location = assigned },
            new ProductLocationAssignment { Product = product, Location = both },
            new ProductLocationAssignment { Product = product, Location = inactiveAssignment, IsActive = false },
            new ProductLocationAssignment { Product = product, Location = blocked },
            new ProductLocationAssignment { Product = inactiveProduct, Location = both });
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = product, Location = both, Quantity = 3m },
            new InventoryBalance { Product = product, Location = balanceOnly, Quantity = -1m });
        await db.SaveChangesAsync();
        var assignmentsBefore = await db.ProductLocationAssignments.CountAsync();
        var balancesBefore = await db.InventoryBalances.CountAsync();
        var queries = new InventoryQueryService(db);

        var productPositions = await queries.GetProductInventoryAsync(product.Id);
        Assert.Equal(["PUBLIC-A", "PUBLIC-B", "PUBLIC-C", "PUBLIC-E"], productPositions.Select(item => item.LocationCode));
        Assert.True(productPositions.Single(item => item.LocationCode == "PUBLIC-A").HasActiveAssignment);
        Assert.Equal(0m, productPositions.Single(item => item.LocationCode == "PUBLIC-A").Quantity);
        Assert.True(productPositions.Single(item => item.LocationCode == "PUBLIC-B").HasNonZeroBalance);
        Assert.True(productPositions.Single(item => item.LocationCode == "PUBLIC-C").IsNegative);
        Assert.True(productPositions.Single(item => item.LocationCode == "PUBLIC-E").LocationIsBlocked);

        var locationPositions = await queries.GetLocationInventoryAsync(both.Id);
        Assert.Equal(["PUBLIC-INACTIVE", "PUBLIC-MAIN"], locationPositions.Select(item => item.ProductSku));
        Assert.False(locationPositions.Single(item => item.ProductSku == "PUBLIC-INACTIVE").ProductIsActive);
        Assert.Equal(3m, locationPositions.Single(item => item.ProductSku == "PUBLIC-MAIN").Quantity);
        Assert.Equal(assignmentsBefore, await db.ProductLocationAssignments.CountAsync());
        Assert.Equal(balancesBefore, await db.InventoryBalances.CountAsync());
    }

    [Fact]
    public async Task Inventory_search_and_resolution_include_inactive_products_and_blocked_locations()
    {
        await using var db = CreateDbContext();
        var inactiveProduct = new Product { Sku = "INVENTORY-INACTIVE", BaseUnitId = 1, IsActive = false };
        var blockedLocation = new Location { Code = "INVENTORY-BLOCKED", Kind = LocationKind.Area, IsBlocked = true };
        db.AddRange(inactiveProduct, blockedLocation);
        await db.SaveChangesAsync();
        var service = new OperationalInventoryQueryService(db);

        var results = await service.SearchInventoryAsync("inventory");
        Assert.Contains(results.Products, item => item.Id == inactiveProduct.Id && !item.IsActive);
        Assert.Contains(results.Locations, item => item.Id == blockedLocation.Id && item.IsBlocked);

        Assert.Equal(inactiveProduct.Id, (await service.ResolveInventoryCodeAsync(" inventory-inactive ")).Product?.Id);
        Assert.Equal(blockedLocation.Id, (await service.ResolveInventoryCodeAsync("inventory-blocked")).Location?.Id);
    }

    [Fact]
    public async Task Inventory_pages_and_alerts_filter_and_summarize_current_balances()
    {
        await using var db = CreateDbContext();
        var product = new Product { Sku = "ALERT-MAIN", BaseUnitId = 1, MinimumStock = 10m };
        var negativeProduct = new Product { Sku = "ALERT-NEGATIVE", BaseUnitId = 1 };
        var assigned = new Location { Code = "ALERT-A", Kind = LocationKind.Area };
        var unassigned = new Location { Code = "ALERT-B", Kind = LocationKind.Area };
        db.AddRange(product, negativeProduct, assigned, unassigned);
        db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = product, Location = assigned });
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = product, Location = assigned, Quantity = 2m },
            new InventoryBalance { Product = product, Location = unassigned, Quantity = -1m },
            new InventoryBalance { Product = negativeProduct, Location = unassigned, Quantity = -3m });
        await db.SaveChangesAsync();
        var service = new InventoryQueryService(db);

        var positions = await service.GetProductInventoryPageAsync(product.Id, InventoryPositionFilter.UnassignedBalance, 1, 25);
        Assert.Single(positions.Items);
        Assert.Equal(unassigned.Id, positions.Items[0].LocationId);
        Assert.Equal(2, positions.Summary.Positions);
        Assert.Equal(1, positions.Summary.Negative);
        Assert.Equal(1, positions.Summary.UnassignedBalances);

        var summary = await service.GetAlertSummaryAsync();
        Assert.Equal(2, summary.NegativePositions);
        Assert.Equal(2, summary.NegativeProducts);
        Assert.Equal(2, summary.BelowMinimumProducts);

        var negativeAlerts = await service.GetNegativeAlertPageAsync("alert-b", 1, 25);
        Assert.Equal(2, negativeAlerts.TotalCount);
        Assert.All(negativeAlerts.Items, item => Assert.Equal("ALERT-B", item.LocationCode));
        var minimumAlerts = await service.GetBelowMinimumAlertPageAsync("alert-main", 1, 25);
        var minimum = Assert.Single(minimumAlerts.Items);
        Assert.Equal(9m, minimum.Deficit);
        Assert.Equal(10m, minimum.CoveragePercent);
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
