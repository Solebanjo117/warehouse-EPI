using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Catalogs;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Locations;

namespace WarehouseEPI.Tests.Web;

public sealed class ProductWarehouseVisibilityTests
{
    [Theory]
    [InlineData("entry")]
    [InlineData("exit")]
    [InlineData("transfer")]
    [InlineData("wipissue")]
    [InlineData("adjustment")]
    public async Task Operation_related_locations_exclude_wip_with_balance_or_default_assignment(string operation)
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var product = new Product { Sku = "OPERATION-SKU", BaseUnitId = 1 };
        var storage = new Location { Code = "K-12-1", Kind = LocationKind.Area };
        var wip = new Location { Code = "M-1-1", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var emptyWip = new Location { Code = "M-2-3", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        product.DefaultEntryLocation = emptyWip;
        db.AddRange(product, storage, wip, emptyWip);
        db.ProductLocationAssignments.AddRange(
            new ProductLocationAssignment { Product = product, Location = storage },
            new ProductLocationAssignment { Product = product, Location = emptyWip });
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = product, Location = storage, Quantity = 4000 },
            new InventoryBalance { Product = product, Location = wip, Quantity = 12000 });
        await db.SaveChangesAsync();

        var query = new OperationalInventoryQueryService(db);
        var lookup = new WarehouseEPI.Web.Pages.Operations.LookupModel(query, new InventoryQueryService(db));
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(
            await lookup.OnGetProductLocationsAsync(product.Id, operation, CancellationToken.None));
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<OperationalProductLocationResult>>(response.Value);
        Assert.Equal(storage.Id, Assert.Single(suggestions).Id);
        Assert.Equal(4000m, suggestions[0].Quantity);
        Assert.False(suggestions[0].IsWip);
        Assert.Equal(2, (await query.SearchWipLocationsAsync("M-")).Count);

        db.InventoryBalances.Remove(await db.InventoryBalances.SingleAsync(balance => balance.LocationId == storage.Id));
        await db.SaveChangesAsync();
        var onlyWipResponse = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(
            await lookup.OnGetProductLocationsAsync(product.Id, operation, CancellationToken.None));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<OperationalProductLocationResult>>(onlyWipResponse.Value));
    }

    [Theory]
    [InlineData(25, 100)]
    [InlineData(0, 100)]
    [InlineData(25, -10)]
    [InlineData(25, 0)]
    public async Task Product_queries_exclude_wip_from_positions_totals_and_lots(decimal warehouse, decimal production)
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var product = new Product { Sku = "WAREHOUSE-SKU", BaseUnitId = 1 };
        var storage = new Location { Code = "STORAGE", Kind = LocationKind.Area };
        var wip = new Location { Code = "PRODUCTION", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var lot = new ProductLot { Product = product, Number = "LOT", NormalizedNumber = "LOT" };
        db.AddRange(product, storage, wip, lot);
        db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = product, Location = wip });
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = product, Location = storage, LotId = lot.Id, Quantity = warehouse },
            new InventoryBalance { Product = product, Location = wip, LotId = lot.Id, Quantity = production });
        await db.SaveChangesAsync();

        var inventory = new InventoryQueryService(db);
        var page = await inventory.GetProductInventoryPageAsync(product.Id, InventoryPositionFilter.All, 1, 25, wip.Id);
        Assert.Equal(warehouse == 0 ? 0 : 1, page.TotalCount);
        Assert.Equal(warehouse == 0 ? 0 : 1, page.Summary.WithBalance);
        Assert.Equal(0, page.Summary.Negative);
        Assert.Equal(0, page.Summary.AssignedZero);
        Assert.Equal(0, page.Summary.ActiveAssignments);
        Assert.DoesNotContain(await inventory.GetProductInventoryAsync(product.Id), row => row.LocationId == wip.Id);
        Assert.DoesNotContain(await inventory.GetProductBalancesAsync(product.Id), row => row.LocationId == wip.Id);
        Assert.Equal(warehouse, await inventory.GetProductTotalAsync(product.Id));
        // WIP remains queryable directly, with its actual balance and assignment.
        Assert.Equal(production, Assert.Single(await inventory.GetLocationInventoryAsync(wip.Id)).Quantity);
        Assert.Equal(production, (await inventory.GetBalanceAsync(product.Id, wip.Id)).Quantity);

        var catalog = new ProductCatalogQueryService(db, new WarehouseSettingsService(db));
        var detail = await catalog.GetAsync(product.Id);
        Assert.NotNull(detail);
        Assert.Equal(warehouse, detail.Quantity);
        Assert.Equal(0, detail.ActiveAssignments);
        Assert.Equal(warehouse == 0 ? 0 : 1, detail.LocationsWithBalance);
        Assert.DoesNotContain(detail.Locations, row => row.Id == wip.Id);
        Assert.Equal(warehouse, Assert.Single(detail.Lots).Quantity);
        var row = Assert.Single((await catalog.SearchAsync(new(product.Sku), 1, 25)).Items);
        Assert.Equal(warehouse, row.Quantity);
        Assert.False(row.HasAssignment);
        Assert.Equal(2, await db.InventoryBalances.CountAsync());
        Assert.Equal(1, await db.ProductLocationAssignments.CountAsync());
    }

    [Theory]
    [InlineData("FIND-SKU", "table")]
    [InlineData("Unique material", "table")]
    [InlineData("REF-FIND", "table")]
    [InlineData("987654321", "table")]
    [InlineData("FIND-SKU", "map")]
    [InlineData("Unique material", "map")]
    [InlineData("REF-FIND", "map")]
    [InlineData("987654321", "map")]
    public async Task Location_product_search_excludes_wip_but_direct_location_search_keeps_it(string search, string view)
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var product = new Product { Sku = "FIND-SKU", Description = "Unique material", ExternalReference = "REF-FIND", BaseUnitId = 1 };
        var storage = new Location { Code = "STORAGE", Kind = LocationKind.Area };
        var wip = new Location { Code = "PRODUCTION", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        var assignedWip = new Location { Code = "PRODUCTION-ZERO", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
        db.AddRange(product, storage, wip, assignedWip);
        db.ProductBarcodes.Add(new ProductBarcode { Product = product, Barcode = "987654321" });
        db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = product, Location = assignedWip });
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = product, Location = storage, Quantity = 25 },
            new InventoryBalance { Product = product, Location = wip, Quantity = 100 });
        await db.SaveChangesAsync();

        var model = new LocationIndexPageModel(db);
        await model.OnGetAsync(search, viewMode: view);
        Assert.Contains(model.Locations, row => row.Id == storage.Id);
        Assert.DoesNotContain(model.Locations, row => row.Id == wip.Id || row.Id == assignedWip.Id);
        if (view == "map")
        {
            Assert.Contains(storage.Id, model.MapMatches);
            Assert.DoesNotContain(wip.Id, model.MapMatches);
            Assert.DoesNotContain(assignedWip.Id, model.MapMatches);
        }
        await model.OnGetAsync(wip.Code, viewMode: view);
        Assert.Contains(model.Locations, row => row.Id == wip.Id);
    }

    [Theory]
    [InlineData("FIND-SKU", "table")]
    [InlineData("Unique material", "table")]
    [InlineData("REF-FIND", "table")]
    [InlineData("987654321", "table")]
    [InlineData("FIND-SKU", "map")]
    [InlineData("Unique material", "map")]
    [InlineData("REF-FIND", "map")]
    [InlineData("987654321", "map")]
    [InlineData("FIND-SKU", "racks")]
    [InlineData("Unique material", "racks")]
    [InlineData("REF-FIND", "racks")]
    [InlineData("987654321", "racks")]
    public async Task Public_location_product_search_requires_net_balance_of_the_matching_product(string search, string view)
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var (product, positions) = await SeedSearchBalancesAsync(db);
        var model = new LocationIndexPageModel(db)
        {
            PageContext = new() { HttpContext = new DefaultHttpContext() }
        };

        await model.OnGetAsync(search, viewMode: view);

        Assert.Equal(positions.Take(3).Select(location => location.Id).Order(), model.Locations.Select(location => location.Id).Order());
        Assert.Equal(12m, Assert.Single(model.Locations.Single(row => row.Id == positions[0].Id).Balances).Quantity);
        Assert.True(model.Locations.Single(row => row.Id == positions[1].Id).HasNegative);
        if (view == "map") Assert.True(model.MapMatches.SetEquals(positions.Take(3).Select(location => location.Id)));

        // A physical location can still be looked up directly when the product is absent.
        await model.OnGetAsync(positions[3].Code, viewMode: view);
        Assert.Equal(positions[3].Id, Assert.Single(model.Locations).Id);
        await model.OnGetAsync("Empty assigned position", viewMode: view);
        Assert.Equal(positions[4].Id, Assert.Single(model.Locations).Id);
        await model.OnGetAsync(null, viewMode: view);
        Assert.Equal(positions.Length, model.Locations.Count);
        Assert.Equal(3, await db.ProductLocationAssignments.CountAsync(assignment => assignment.ProductId == product.Id));
        Assert.Equal(9, await db.InventoryBalances.CountAsync());
    }

    [Theory]
    [InlineData("table")]
    [InlineData("racks")]
    public async Task Administrative_location_search_keeps_active_assignments_without_stock(string view)
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var (product, positions) = await SeedSearchBalancesAsync(db);
        var model = new WarehouseEPI.Web.Pages.Admin.Catalogs.Locations.IndexModel(db);

        await model.OnGetAsync(product.Sku, viewMode: view);

        Assert.Contains(model.Locations, row => row.Id == positions[4].Id && row.ProductCount == 1 && !row.HasInventory);
    }

    private static async Task<(Product Product, Location[] Positions)> SeedSearchBalancesAsync(WarehouseDbContext db)
    {
        var product = new Product { Sku = "FIND-SKU", Description = "Unique material", ExternalReference = "REF-FIND", BaseUnitId = 1 };
        var other = new Product { Sku = "OTHER-SKU", BaseUnitId = 1 };
        var positions = Enumerable.Range(1, 7).Select(pallet => new Location
        {
            Code = $"A-1-{pallet}", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 1, PalletNumber = (short)pallet
        }).ToArray();
        positions[4].Description = "Empty assigned position";
        var firstLot = new ProductLot { Product = product, Number = "FIRST", NormalizedNumber = "FIRST" };
        var secondLot = new ProductLot { Product = product, Number = "SECOND", NormalizedNumber = "SECOND" };
        db.AddRange(product, other, firstLot, secondLot);
        db.AddRange(positions);
        db.ProductBarcodes.Add(new ProductBarcode { Product = product, Barcode = "987654321" });
        db.ProductLocationAssignments.AddRange(
            new ProductLocationAssignment { Product = product, Location = positions[0] },
            new ProductLocationAssignment { Product = product, Location = positions[3], IsActive = false },
            new ProductLocationAssignment { Product = product, Location = positions[4] });
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = product, Location = positions[0], Lot = firstLot, Quantity = 5 },
            new InventoryBalance { Product = product, Location = positions[0], Lot = secondLot, Quantity = 7 },
            new InventoryBalance { Product = product, Location = positions[1], Quantity = -3 },
            new InventoryBalance { Product = product, Location = positions[2], Quantity = 8 },
            new InventoryBalance { Product = other, Location = positions[2], Quantity = -8 },
            new InventoryBalance { Product = product, Location = positions[3], Quantity = 0 },
            new InventoryBalance { Product = product, Location = positions[5], Lot = firstLot, Quantity = 8 },
            new InventoryBalance { Product = product, Location = positions[5], Lot = secondLot, Quantity = -8 },
            new InventoryBalance { Product = other, Location = positions[6], Quantity = 99 });
        await db.SaveChangesAsync();
        return (product, positions);
    }

    private static WarehouseDbContext CreateContext() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
