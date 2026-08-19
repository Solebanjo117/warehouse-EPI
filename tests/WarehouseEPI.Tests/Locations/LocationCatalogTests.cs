using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Locations;
using WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

namespace WarehouseEPI.Tests.Locations;

public sealed class LocationCatalogTests
{
    [Fact]
    public void Locations_page_has_a_single_public_constructor_for_razor_activation() =>
        Assert.Single(typeof(IndexModel).GetConstructors());

    [Fact]
    public async Task Warehouse_map_does_not_require_a_client_concurrency_token()
    {
        await using var fixture = new Fixture();
        var layout = fixture.Db.Model.FindEntityType(typeof(WarehouseMapLayout));

        Assert.False(layout!.FindProperty(nameof(WarehouseMapLayout.Version))!.IsConcurrencyToken);
        Assert.Null(layout.FindProperty(nameof(WarehouseMapLayout.RowVersion)));
    }

    [Fact]
    public void PostgreSql_model_matches_the_migration_snapshot()
    {
        using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .Options);
        var snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(
            db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model, designTime: true);
        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshot.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        var description = string.Join(", ", differences.Select(operation => operation is AlterColumnOperation column
            ? $"{operation.GetType().Name}:{column.Table}.{column.Name}:type={column.ColumnType}/{column.OldColumn.ColumnType}:nullable={column.IsNullable}/{column.OldColumn.IsNullable}:default={column.DefaultValueSql}/{column.OldColumn.DefaultValueSql}:computed={column.ComputedColumnSql}/{column.OldColumn.ComputedColumnSql}:annotations={string.Join('|', column.GetAnnotations().Select(item => item.Name + '=' + item.Value))}/{string.Join('|', column.OldColumn.GetAnnotations().Select(item => item.Name + '=' + item.Value))}"
            : operation.GetType().Name));

        Assert.True(differences.Count == 0, description);
    }

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
    public async Task Warehouse_map_proposal_uses_physical_rows_and_keeps_unknown_rows_unplaced()
    {
        await using var fixture = new Fixture();
        fixture.Db.Locations.AddRange(Rack("A", 1, 1), Rack("A", 2, 1), Rack("Z", 1, 1), new Location { Code = "SHIPPING", Kind = LocationKind.Area });
        await fixture.Db.SaveChangesAsync();
        var map = await new WarehouseMapService(fixture.Db).GetAsync(true);
        Assert.False(map.IsInitialized);
        Assert.Empty(fixture.Db.WarehouseMapElements);
        var first = map.Elements.Single(item => item.Label == "A-1");
        var second = map.Elements.Single(item => item.Label == "A-2");
        Assert.True(second.X < first.X);
        Assert.Contains(map.Elements, item => item.Label == "SHIPPING");
        Assert.Contains(map.Unplaced, item => item.Label == "Z-1");
    }

    [Fact]
    public async Task Warehouse_map_initialization_requires_admin_pin_and_is_audited()
    {
        await using var fixture = new Fixture();
        var protector = new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        var pins = new UserPinService(fixture.Db, protector);
        var role = new Role { Id = 1, Code = "ADMIN", Name = "Administrador" };
        var user = new User { FullName = "Map Admin", Role = role, PinLookup = string.Empty, PinHash = string.Empty };
        await pins.AssignAsync(user, "1234"); fixture.Db.AddRange(role, user, Rack("A", 1, 1)); await fixture.Db.SaveChangesAsync();
        var service = new WarehouseMapService(fixture.Db, pins, TimeProvider.System); var operation = Guid.NewGuid();
        Assert.Equal(WarehouseMapSaveStatus.InvalidPin, (await service.InitializeAsync(operation, user.Id, "9999", null)).Status);
        var result = await service.InitializeAsync(operation, user.Id, "1234", "Plano inicial");
        Assert.Equal(WarehouseMapSaveStatus.Success, result.Status);
        Assert.Equal(1, result.Version);
        Assert.Single(fixture.Db.WarehouseMapRevisions);
        Assert.NotEmpty(fixture.Db.WarehouseMapElements);
        Assert.Equal(WarehouseMapSaveStatus.Success, (await service.InitializeAsync(operation, user.Id, "1234", "Plano inicial")).Status);
    }

    [Fact]
    public async Task Warehouse_map_lists_catalog_racks_added_after_initialization_without_writing()
    {
        await using var fixture = new Fixture();
        var protector = new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        var pins = new UserPinService(fixture.Db, protector);
        var role = new Role { Id = 1, Code = "ADMIN", Name = "Administrador" };
        var user = new User { FullName = "Map Admin", Role = role, PinLookup = string.Empty, PinHash = string.Empty };
        await pins.AssignAsync(user, "1234");
        fixture.Db.AddRange(role, user, Rack("A", 1, 1));
        await fixture.Db.SaveChangesAsync();
        var service = new WarehouseMapService(fixture.Db, pins, TimeProvider.System);
        Assert.Equal(WarehouseMapSaveStatus.Success, (await service.InitializeAsync(Guid.NewGuid(), user.Id, "1234", "Inicial")).Status);
        await using (var writer = fixture.CreateDb())
        {
            writer.Locations.Add(Rack("A", 2, 1));
            await writer.SaveChangesAsync();
        }
        var map = await service.GetAsync(true);
        var added = Assert.Single(map.Unplaced, item => item.Label == "A-2");
        Assert.DoesNotContain(await fixture.Db.WarehouseMapElements.ToListAsync(), item => item.Id == added.Id);
        Assert.Single(await fixture.Db.WarehouseMapRevisions.ToListAsync());
    }

    [Fact]
    public async Task Warehouse_map_rejects_unknown_elements_without_creating_a_revision()
    {
        await using var fixture = new Fixture();
        var protector = new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        var pins = new UserPinService(fixture.Db, protector);
        var role = new Role { Id = 1, Code = "ADMIN", Name = "Administrador" };
        var user = new User { FullName = "Map Admin", Role = role, PinLookup = string.Empty, PinHash = string.Empty };
        await pins.AssignAsync(user, "1234");
        fixture.Db.AddRange(role, user, Rack("A", 1, 1));
        await fixture.Db.SaveChangesAsync();
        var service = new WarehouseMapService(fixture.Db, pins, TimeProvider.System);
        Assert.Equal(WarehouseMapSaveStatus.Success, (await service.InitializeAsync(Guid.NewGuid(), user.Id, "1234", "Inicial")).Status);
        fixture.Db.ChangeTracker.Clear();
        var map = await service.GetAsync(true);
        var geometry = map.Elements.Select(item => new WarehouseMapGeometry(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible))
            .Append(new WarehouseMapGeometry(Guid.NewGuid(), 10, 10, 20, 20, 0, 99, true)).ToArray();

        var result = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", null, geometry));
        Assert.Equal(WarehouseMapSaveStatus.ValidationFailed, result.Status);
        Assert.Single(await fixture.Db.WarehouseMapRevisions.ToListAsync());
    }

    [Fact]
    public async Task Warehouse_map_save_increments_the_current_revision_without_a_client_version()
    {
        await using var fixture = new Fixture();
        var protector = new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        var pins = new UserPinService(fixture.Db, protector);
        var role = new Role { Id = 1, Code = "ADMIN", Name = "Administrador" };
        var user = new User { FullName = "Map Admin", Role = role, PinLookup = string.Empty, PinHash = string.Empty };
        await pins.AssignAsync(user, "1234");
        fixture.Db.AddRange(role, user, Rack("A", 1, 1));
        await fixture.Db.SaveChangesAsync();
        var service = new WarehouseMapService(fixture.Db, pins, TimeProvider.System);
        Assert.Equal(WarehouseMapSaveStatus.Success, (await service.InitializeAsync(Guid.NewGuid(), user.Id, "1234", "Inicial")).Status);
        fixture.Db.ChangeTracker.Clear();
        var map = await service.GetAsync(true);
        var geometry = map.Elements.Select(item => new WarehouseMapGeometry(item.Id, item.X + 1, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible)).ToArray();

        var result = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", "Ajuste", geometry));

        Assert.Equal(WarehouseMapSaveStatus.Success, result.Status);
        Assert.Equal(map.Version + 1, result.Version);
        Assert.Equal(map.Version + 1, (await service.GetAsync(true)).Version);
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
        Assert.Equal("racks", page.ViewMode);
        Assert.Equal("C", rack.RowCode);
        Assert.Equal((short)9, Assert.IsType<IndexModel.LocationRow>(rack.Positions[2]).PalletNumber);
        Assert.Contains("FIND-ME", rack.Positions[2]!.Skus);
        Assert.DoesNotContain(page.Locations, candidate => candidate.Id == lower.Id || candidate.Id == unrelated.Id);
    }

    [Fact]
    public async Task Rack_view_groups_non_zero_balances_and_exposes_operational_states_without_areas()
    {
        await using var fixture = new Fixture();
        var each = new Unit { Code = "EA", Name = "Each" };
        var kilograms = new Unit { Code = "KG", Name = "Kilograms" };
        var positiveProduct = new Product { Sku = "RACK-EA", Description = "Existencia positiva", BaseUnit = each };
        var negativeProduct = new Product { Sku = "RACK-KG", Description = "Existencia negativa", BaseUnit = kilograms };
        var positive = Rack("R", 1, 1);
        var negative = Rack("R", 1, 2);
        var zero = Rack("R", 1, 3);
        var blocked = Rack("R", 1, 4); blocked.IsBlocked = true; blocked.BlockReason = "Conteo";
        var inactive = Rack("R", 1, 5); inactive.IsActive = false;
        var area = new Location { Code = "AREA-RACKS", Kind = LocationKind.Area };
        fixture.Db.AddRange(each, kilograms, positiveProduct, negativeProduct, positive, negative, zero, blocked, inactive, area);
        fixture.Db.InventoryBalances.AddRange(
            new InventoryBalance { Product = positiveProduct, Location = positive, Quantity = 4m },
            new InventoryBalance { Product = negativeProduct, Location = negative, Quantity = -2.5m },
            new InventoryBalance { Product = positiveProduct, Location = zero, Quantity = 0m });
        await fixture.Db.SaveChangesAsync();
        await new ProductLocationAssignmentService(fixture.Db).AssignAsync(positiveProduct.Id, positive.Id);

        var page = new IndexModel(fixture.Db);
        await page.OnGetAsync(null, "all", "all", "racks");

        Assert.Empty(page.LayoutAreas);
        Assert.All(page.Locations, location => Assert.Equal(LocationKind.Rack, location.Kind));
        var rack = Assert.Single(page.LayoutRacks);
        Assert.Equal((short)4, rack.Positions[3]!.PalletNumber);
        Assert.Equal((short)1, rack.Positions[6]!.PalletNumber);
        Assert.Equal((short)3, rack.Positions[8]!.PalletNumber);
        Assert.Equal(2, rack.OccupiedCount);
        Assert.Equal(3, rack.IssueCount);
        var positiveRow = Assert.Single(page.Locations, location => location.Id == positive.Id);
        Assert.True(positiveRow.HasInventory);
        Assert.True(Assert.Single(positiveRow.Balances).IsAssigned);
        var negativeRow = Assert.Single(page.Locations, location => location.Id == negative.Id);
        Assert.True(negativeRow.HasNegative);
        Assert.True(negativeRow.HasIssue);
        Assert.False(Assert.Single(page.Locations, location => location.Id == zero.Id).HasInventory);

        await page.OnGetAsync(null, "all", "all", "racks", rackFilter: "issues");
        Assert.Equal("issues", page.RackFilter);
        Assert.Single(page.LayoutRacks);
    }

    [Fact]
    public async Task Table_pagination_keeps_the_selected_view_and_uses_twenty_five_rows()
    {
        await using var fixture = new Fixture();
        fixture.Db.Locations.AddRange(Enumerable.Range(1, 55)
            .Select(number => new Location { Code = $"AREA-{number}", Kind = LocationKind.Area }));
        await fixture.Db.SaveChangesAsync();

        var page = new IndexModel(fixture.Db);
        await page.OnGetAsync(null, "all", "all", "table", null, 2);

        Assert.Equal("table", page.ViewMode);
        Assert.Equal(2, page.CurrentPage);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(25, page.Locations.Count);
        Assert.Equal([1, 2, 3], page.VisiblePages);
    }

    [Fact]
    public async Task Location_detail_loads_grouped_balances_and_assignment_state_separately()
    {
        await using var fixture = new Fixture();
        var unit = new Unit { Code = "EA", Name = "Each" };
        var product = new Product { Sku = "DETAIL-BALANCE", BaseUnit = unit };
        var location = Rack("E", 1, 1);
        fixture.Db.AddRange(unit, product, location);
        fixture.Db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = location, Quantity = 12m });
        await fixture.Db.SaveChangesAsync();
        await new ProductLocationAssignmentService(fixture.Db).AssignAsync(product.Id, location.Id);

        var page = new DetailsModel(fixture.Db, new ProductLocationAssignmentService(fixture.Db));
        var result = await page.OnGetAsync(location.Id, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        var balance = Assert.Single(page.Balances);
        Assert.Equal(12m, balance.Quantity);
        Assert.True(balance.IsAssigned);
    }

    private static Location Rack(string row, short rack, short pallet) => new()
    {
        Code = $"{row}-{rack}-{pallet}",
        Kind = LocationKind.Rack,
        RowCode = row,
        RackNumber = rack,
        PalletNumber = pallet
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly InMemoryDatabaseRoot databaseRoot = new();
        private readonly string databaseName = Guid.NewGuid().ToString();
        public Guid Owner { get; } = Guid.NewGuid();
        public DbContextOptions<WarehouseDbContext> Options { get; }
        public WarehouseDbContext Db { get; }
        public LocationGenerationService Service { get; }

        public Fixture()
        {
            Options = new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase(databaseName, databaseRoot).Options;
            Db = new WarehouseDbContext(Options);
            Service = new LocationGenerationService(Db, new LocationGenerationPreviewStore(cache, TimeProvider.System));
        }

        public WarehouseDbContext CreateDb() => new(Options);

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); cache.Dispose(); }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current += value;
    }
}
