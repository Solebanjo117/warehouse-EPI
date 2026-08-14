using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Locations;
using WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

namespace WarehouseEPI.Tests.Locations;

public sealed class LocationCatalogTests
{
    [Theory]
    [InlineData(1, 1, "Inferior")]
    [InlineData(3, 1, "Inferior")]
    [InlineData(4, 2, "Medio")]
    [InlineData(6, 2, "Medio")]
    [InlineData(7, 3, "Superior")]
    [InlineData(9, 3, "Superior")]
    public void Pallet_uses_numeric_keypad_levels(short pallet, short level, string levelName)
    {
        var location = new Location { Code = $"A-1-{pallet}", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 1, PalletNumber = pallet };
        Assert.Equal(level, location.LevelNumber);
        Assert.Equal(levelName, LocationNormalization.LevelName(pallet));
        Assert.True(LocationNormalization.IsStructurallyValid(location));
    }

    [Theory]
    [InlineData(" a-1-8 ", "A-1-8")]
    [InlineData(" shipping ", "SHIPPING")]
    public void Scan_lookup_normalizes_trim_and_case(string input, string expected) =>
        Assert.Equal(expected, LocationNormalization.NormalizeForLookup(input));

    [Fact]
    public void Area_and_rack_require_distinct_structures()
    {
        Assert.True(LocationNormalization.IsStructurallyValid(new Location { Code = "WIP-2", Kind = LocationKind.Area }));
        Assert.False(LocationNormalization.IsStructurallyValid(new Location { Code = "A-1-0", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 1, PalletNumber = 0 }));
        Assert.False(LocationNormalization.IsValidAreaCode("-SHIPPING"));
    }

    [Fact]
    public async Task Preview_supports_multiple_blocks_and_incomplete_racks_without_writing()
    {
        await using var fixture = new Fixture();
        var preview = await fixture.Service.PrepareAsync(" a,1,2,1-3\nB,4,4,1;5;9", fixture.Owner);
        Assert.True(preview.CanConfirm);
        Assert.Equal(9, preview.Rows.Count);
        Assert.Equal(3, preview.RackCount);
        Assert.Contains(preview.Rows, row => row.Code == "B-4-9" && row.Level == "Superior");
        Assert.Empty(await fixture.Db.Locations.ToListAsync());
    }

    [Fact]
    public async Task Preview_rejects_invalid_and_duplicate_blocks()
    {
        await using var fixture = new Fixture();
        var preview = await fixture.Service.PrepareAsync("AA,1,2,1-9\nA,1,1,1-3\nA,1,1,1-3", fixture.Owner);
        Assert.False(preview.CanConfirm);
        Assert.Contains(preview.Errors, error => error.Contains("fila debe ser una letra"));
        Assert.Contains(preview.Errors, error => error.Contains("repetidos"));
    }

    [Fact]
    public async Task Preview_is_owner_bound_and_confirmation_is_one_use()
    {
        await using var fixture = new Fixture();
        var preview = await fixture.Service.PrepareAsync("C,1,1,1;5;9", fixture.Owner);
        Assert.False(fixture.Service.TryGetPreview(preview.Token, Guid.NewGuid(), out _));
        var result = await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner, ["C-1-1", "C-1-9"]);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(2, await fixture.Db.Locations.CountAsync());
        Assert.False((await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner, ["C-1-1"])).Succeeded);
    }

    [Fact]
    public async Task Preview_detects_existing_code_and_lookup_resolves_scan()
    {
        await using var fixture = new Fixture();
        fixture.Db.Locations.Add(new Location { Code = "D-1-8", Kind = LocationKind.Rack, RowCode = "D", RackNumber = 1, PalletNumber = 8 });
        await fixture.Db.SaveChangesAsync();
        var preview = await fixture.Service.PrepareAsync("D,1,1,7-9", fixture.Owner);
        Assert.False(preview.CanConfirm);
        Assert.Equal(1, preview.ExistingCount);
        var lookup = new LocationLookupService(fixture.Db);
        Assert.Equal("D-1-8", (await lookup.FindByCodeAsync(" d-1-8 "))?.Code);
    }

    [Fact]
    public void Preview_expires_after_thirty_minutes()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var store = new LocationGenerationPreviewStore(cache, clock);
        var owner = Guid.NewGuid();
        var preview = store.Save(owner, "A,1,1,1-9", [], []);
        Assert.True(store.TryGet(preview.Token, owner, out _));
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(store.TryGet(preview.Token, owner, out _));
    }

    [Fact]
    public async Task Blocking_requires_reason_and_deactivation_clears_block()
    {
        await using var fixture = new Fixture();
        var location = new Location { Code = "HOLD", Kind = LocationKind.Area };
        fixture.Db.Locations.Add(location);
        await fixture.Db.SaveChangesAsync();
        var page = new IndexModel(fixture.Db);
        await page.OnPostBlockAsync(location.Id, " ", default);
        Assert.False(location.IsBlocked);
        await page.OnPostBlockAsync(location.Id, "Mantenimiento", default);
        Assert.True(location.IsBlocked);
        Assert.Equal("Mantenimiento", location.BlockReason);
        await page.OnPostToggleAsync(location.Id, default);
        Assert.False(location.IsActive);
        Assert.False(location.IsBlocked);
        Assert.Null(location.BlockReason);
    }

    [Fact]
    public async Task Fixed_assignments_are_many_to_many_and_reactivate_without_duplicates()
    {
        await using var fixture = new Fixture();
        var unit = new Unit { Code = "EA", Name = "Each" };
        var firstProduct = new Product { Sku = "PRODUCT-A", BaseUnit = unit };
        var secondProduct = new Product { Sku = "PRODUCT-B", BaseUnit = unit };
        var firstLocation = Rack("A", 1, 1);
        var secondLocation = Rack("A", 1, 2);
        fixture.Db.AddRange(firstProduct, secondProduct, firstLocation, secondLocation);
        await fixture.Db.SaveChangesAsync();
        var assignments = new ProductLocationAssignmentService(fixture.Db);

        Assert.Equal(ProductLocationAssignmentResult.Success,
            await assignments.AssignAsync(firstProduct.Id, firstLocation.Id));
        Assert.Equal(ProductLocationAssignmentResult.Success,
            await assignments.AssignAsync(firstProduct.Id, secondLocation.Id));
        Assert.Equal(ProductLocationAssignmentResult.Success,
            await assignments.AssignAsync(secondProduct.Id, firstLocation.Id));
        Assert.Equal(3, await fixture.Db.ProductLocationAssignments.CountAsync());

        Assert.Equal(ProductLocationAssignmentResult.Success,
            await assignments.DeactivateAsync(firstProduct.Id, firstLocation.Id));
        Assert.Equal(ProductLocationAssignmentResult.Success,
            await assignments.AssignAsync(firstProduct.Id, firstLocation.Id));
        Assert.Equal(3, await fixture.Db.ProductLocationAssignments.CountAsync());
        Assert.True((await fixture.Db.ProductLocationAssignments.FindAsync(firstProduct.Id, firstLocation.Id))!.IsActive);
    }

    [Fact]
    public async Task Assignment_rejects_non_operational_records_but_survives_later_state_changes()
    {
        await using var fixture = new Fixture();
        var unit = new Unit { Code = "EA", Name = "Each" };
        var product = new Product { Sku = "PRODUCT-C", BaseUnit = unit };
        var location = Rack("B", 2, 8);
        fixture.Db.AddRange(product, location);
        await fixture.Db.SaveChangesAsync();
        var assignments = new ProductLocationAssignmentService(fixture.Db);

        Assert.Equal(ProductLocationAssignmentResult.Success,
            await assignments.AssignAsync(product.Id, location.Id));
        product.IsActive = false;
        location.IsBlocked = true;
        location.BlockReason = "Mantenimiento";
        await fixture.Db.SaveChangesAsync();
        Assert.True((await fixture.Db.ProductLocationAssignments.SingleAsync()).IsActive);

        var otherLocation = Rack("B", 2, 9);
        fixture.Db.Locations.Add(otherLocation);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(ProductLocationAssignmentResult.ProductInactive,
            await assignments.AssignAsync(product.Id, otherLocation.Id));
        product.IsActive = true;
        otherLocation.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(ProductLocationAssignmentResult.LocationInactive,
            await assignments.AssignAsync(product.Id, otherLocation.Id));
        otherLocation.IsActive = true;
        otherLocation.IsBlocked = true;
        otherLocation.BlockReason = "Conteo";
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(ProductLocationAssignmentResult.LocationBlocked,
            await assignments.AssignAsync(product.Id, otherLocation.Id));
    }

    [Fact]
    public async Task Layout_and_product_search_return_only_assigned_racks_in_keypad_order()
    {
        await using var fixture = new Fixture();
        var unit = new Unit { Code = "EA", Name = "Each" };
        var product = new Product { Sku = "FIND-ME", Description = "Producto localizado", BaseUnit = unit };
        var lower = Rack("C", 3, 1);
        var upper = Rack("C", 3, 9);
        var unrelated = Rack("D", 1, 1);
        fixture.Db.AddRange(product, lower, upper, unrelated);
        await fixture.Db.SaveChangesAsync();
        var assignments = new ProductLocationAssignmentService(fixture.Db);
        await assignments.AssignAsync(product.Id, upper.Id);

        var page = new IndexModel(fixture.Db);
        await page.OnGetAsync("find-me", "all", "all", "layout", null, 1);

        var rack = Assert.Single(page.LayoutRacks);
        Assert.Equal("C", rack.RowCode);
        Assert.Equal((short)9, Assert.IsType<IndexModel.LocationRow>(rack.Positions[2]).PalletNumber);
        Assert.Contains("FIND-ME", rack.Positions[2]!.Skus);
        Assert.DoesNotContain(page.Locations, candidate => candidate.Id == lower.Id || candidate.Id == unrelated.Id);
    }

    [Fact]
    public async Task Table_pagination_keeps_the_selected_view_and_uses_fifty_rows()
    {
        await using var fixture = new Fixture();
        fixture.Db.Locations.AddRange(Enumerable.Range(1, 55)
            .Select(number => new Location { Code = $"AREA-{number}", Kind = LocationKind.Area }));
        await fixture.Db.SaveChangesAsync();

        var page = new IndexModel(fixture.Db);
        await page.OnGetAsync(null, "all", "all", "table", null, 2);

        Assert.Equal("table", page.ViewMode);
        Assert.Equal(2, page.CurrentPage);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(5, page.Locations.Count);
        Assert.Equal([1, 2], page.VisiblePages);
    }

    private static Location Rack(string row, short rack, short pallet) => new()
    {
        Code = $"{row}-{rack}-{pallet}", Kind = LocationKind.Rack,
        RowCode = row, RackNumber = rack, PalletNumber = pallet
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        public Guid Owner { get; } = Guid.NewGuid();
        public WarehouseDbContext Db { get; }
        public LocationGenerationService Service { get; }

        public Fixture()
        {
            Db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Service = new LocationGenerationService(Db, new LocationGenerationPreviewStore(cache, TimeProvider.System));
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); cache.Dispose(); }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current += value;
    }
}
