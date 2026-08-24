using System.Text.Json;
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
    public async Task Warehouse_map_architecture_model_is_independent_from_locations()
    {
        await using var fixture = new Fixture();
        var architectural = fixture.Db.Model.FindEntityType(typeof(WarehouseMapArchitecturalElement));
        var layer = fixture.Db.Model.FindEntityType(typeof(WarehouseMapLayer));

        Assert.NotNull(architectural);
        Assert.NotNull(layer);
        Assert.Null(architectural!.FindNavigation("Location"));
        Assert.NotNull(architectural.FindProperty(nameof(WarehouseMapArchitecturalElement.GeometryJson)));
        Assert.Contains(architectural.GetForeignKeys(), foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(WarehouseMapLayer));
        Assert.Contains(layer!.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual([nameof(WarehouseMapLayer.LayoutId), nameof(WarehouseMapLayer.Code)]));
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
        Assert.True(map.UsesLegacyArchitecture);
        Assert.Equal(6, map.Layers.Count);
        Assert.Equal(17, map.Architecture.Count);
        Assert.Contains(map.Architecture, item => item.Label == "KPA / Breakroom");
        Assert.Contains(map.Architecture, item => item.Label == "Packing / Producción");
        Assert.Equal(6, map.Architecture.Count(item => item.Kind == "Polyline"));
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
        Assert.True(result.Status == WarehouseMapSaveStatus.Success,
            $"Estado {result.Status}: {string.Join(" | ", result.ValidationErrors)}");
        Assert.Equal(1, result.Version);
        Assert.Single(fixture.Db.WarehouseMapRevisions);
        Assert.NotEmpty(fixture.Db.WarehouseMapElements);
        Assert.Equal(6, await fixture.Db.WarehouseMapLayers.CountAsync());
        Assert.Equal(17, await fixture.Db.WarehouseMapArchitecturalElements.CountAsync());
        Assert.False((await fixture.Db.WarehouseMapLayers.SingleAsync(item => item.Code == WarehouseMapLayerCode.Operations)).IsLocked);
        Assert.All(await fixture.Db.WarehouseMapLayers.Where(item => item.Code != WarehouseMapLayerCode.Operations).ToListAsync(), item => Assert.True(item.IsLocked));
        Assert.Equal(WarehouseMapSaveStatus.Success, (await service.InitializeAsync(operation, user.Id, "1234", "Plano inicial")).Status);
    }

    [Fact]
    public async Task Warehouse_map_first_legacy_architecture_save_is_audited_without_changing_operational_data()
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

        var operationalBefore = await fixture.Db.WarehouseMapElements.AsNoTracking()
            .Select(item => new { item.Id, item.RowCode, item.RackNumber, item.LocationId, item.X, item.Y, item.Width, item.Height })
            .OrderBy(item => item.Id).ToArrayAsync();
        fixture.Db.WarehouseMapArchitecturalElements.RemoveRange(await fixture.Db.WarehouseMapArchitecturalElements.ToListAsync());
        await fixture.Db.SaveChangesAsync();
        fixture.Db.WarehouseMapLayers.RemoveRange(await fixture.Db.WarehouseMapLayers.ToListAsync());
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var legacy = await service.GetAsync(true);
        Assert.True(legacy.UsesLegacyArchitecture);
        var layers = LayerStates(legacy).Select(item => item.Code == "STRUCTURE" ? item with { IsLocked = false } : item).ToArray();
        var architecture = ArchitectureGeometry(legacy);
        architecture[0] = architecture[0] with { X = architecture[0].X + 1 };
        var geometry = legacy.Elements.Concat(legacy.Unplaced).Select(item =>
            new WarehouseMapGeometry(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible)).ToArray();

        var result = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", "Conservar fondo heredado",
            geometry, layers, architecture));

        Assert.True(result.Status == WarehouseMapSaveStatus.Success,
            $"Estado {result.Status}: {string.Join(" | ", result.ValidationErrors)}");
        Assert.Equal(6, await fixture.Db.WarehouseMapLayers.CountAsync());
        Assert.Equal(17, await fixture.Db.WarehouseMapArchitecturalElements.CountAsync());
        Assert.False((await service.GetAsync(true)).UsesLegacyArchitecture);
        var operationalAfter = await fixture.Db.WarehouseMapElements.AsNoTracking()
            .Select(item => new { item.Id, item.RowCode, item.RackNumber, item.LocationId, item.X, item.Y, item.Width, item.Height })
            .OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(operationalBefore, operationalAfter);
        var revision = await fixture.Db.WarehouseMapRevisions.OrderByDescending(item => item.NewVersion).FirstAsync();
        using var changes = JsonDocument.Parse(revision.ChangesJson);
        Assert.Equal(4, changes.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.True(changes.RootElement.TryGetProperty("Operational", out _));
        Assert.True(changes.RootElement.TryGetProperty("Layers", out _));
        Assert.True(changes.RootElement.TryGetProperty("Architecture", out _));
    }

    [Fact]
    public async Task Warehouse_map_rejects_missing_or_invalid_architecture_without_a_revision()
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
        var geometry = map.Elements.Concat(map.Unplaced).Select(item =>
            new WarehouseMapGeometry(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible)).ToArray();

        var missing = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", null, geometry,
            LayerStates(map), ArchitectureGeometry(map).Skip(1).ToArray()));
        Assert.Equal(WarehouseMapSaveStatus.ValidationFailed, missing.Status);

        var invalidArchitecture = ArchitectureGeometry(map);
        invalidArchitecture[0] = invalidArchitecture[0] with { X = 1599, Width = 10 };
        var invalid = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", null, geometry,
            LayerStates(map), invalidArchitecture));
        Assert.Equal(WarehouseMapSaveStatus.ValidationFailed, invalid.Status);

        var omittedPersisted = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", null, geometry,
            LayerStates(map), ArchitectureGeometry(map).SkipLast(1).ToArray()));
        Assert.Equal(WarehouseMapSaveStatus.ValidationFailed, omittedPersisted.Status);
        Assert.Contains(omittedPersisted.ValidationErrors,
            error => error.Contains("No se pueden eliminar", StringComparison.Ordinal));
        Assert.Single(await fixture.Db.WarehouseMapRevisions.ToListAsync());
    }

    [Fact]
    public async Task Warehouse_map_adds_and_edits_architecture_without_changing_operational_elements()
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
        Assert.Equal(WarehouseMapSaveStatus.Success,
            (await service.InitializeAsync(Guid.NewGuid(), user.Id, "1234", "Inicial")).Status);
        fixture.Db.ChangeTracker.Clear();

        var map = await service.GetAsync(true);
        var operationalBefore = await fixture.Db.WarehouseMapElements.AsNoTracking()
            .Select(item => new { item.Id, item.LocationId, item.X, item.Y, item.Width, item.Height })
            .OrderBy(item => item.Id).ToArrayAsync();
        var layers = LayerStates(map).Select(item => item.Code == "ZONES" ? item with { IsLocked = false } : item).ToArray();
        var addedId = Guid.NewGuid();
        var architecture = ArchitectureGeometry(map).Append(new WarehouseMapArchitectureItem(
            addedId, "ZONES", "Rectangle", "Zona nueva", 500, 300, 180, 90, 0, 4, [],
            "WARNING", "WARNING", 2, false, 900, false)).ToArray();
        var geometry = map.Elements.Concat(map.Unplaced).Select(item =>
            new WarehouseMapGeometry(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation,
                item.ZIndex, item.IsVisible)).ToArray();
        var operationId = Guid.NewGuid();

        var result = await service.SaveAsync(new(operationId, user.Id, "1234", "Agregar zona",
            geometry, layers, architecture));
        var repeated = await service.SaveAsync(new(operationId, user.Id, "1234", "Agregar zona",
            geometry, layers, architecture));

        Assert.True(result.Status == WarehouseMapSaveStatus.Success,
            $"Estado {result.Status}: {string.Join(" | ", result.ValidationErrors)}");
        Assert.Equal(result.Version, repeated.Version);
        var added = await fixture.Db.WarehouseMapArchitecturalElements.SingleAsync(item => item.Id == addedId);
        Assert.Equal("WARNING", added.FillToken);
        Assert.Equal(WarehouseMapArchitecturalElementKind.Rectangle, added.Kind);
        Assert.Equal(18, await fixture.Db.WarehouseMapArchitecturalElements.CountAsync());
        fixture.Db.ChangeTracker.Clear();
        var savedMap = await service.GetAsync(true);
        var editedArchitecture = ArchitectureGeometry(savedMap);
        var addedIndex = Array.FindIndex(editedArchitecture, item => item.Id == addedId);
        editedArchitecture[addedIndex] = editedArchitecture[addedIndex] with
        {
            Label = "Zona editada",
            Width = 200,
            FillToken = "PRIMARY"
        };
        var edited = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", "Editar zona",
            geometry, LayerStates(savedMap), editedArchitecture));
        Assert.Equal(WarehouseMapSaveStatus.Success, edited.Status);
        fixture.Db.ChangeTracker.Clear();
        added = await fixture.Db.WarehouseMapArchitecturalElements.SingleAsync(item => item.Id == addedId);
        Assert.Equal("Zona editada", added.Label);
        Assert.Equal("PRIMARY", added.FillToken);
        Assert.Contains("\"width\":200", added.GeometryJson, StringComparison.OrdinalIgnoreCase);
        var operationalAfter = await fixture.Db.WarehouseMapElements.AsNoTracking()
            .Select(item => new { item.Id, item.LocationId, item.X, item.Y, item.Width, item.Height })
            .OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(operationalBefore, operationalAfter);
        var revision = await fixture.Db.WarehouseMapRevisions.SingleAsync(item => item.OperationId == operationId);
        using var changes = JsonDocument.Parse(revision.ChangesJson);
        Assert.Equal(4, changes.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(addedId, changes.RootElement.GetProperty("Architecture").GetProperty("Added")[0].GetGuid());
    }

    [Fact]
    public async Task Warehouse_map_rejects_new_architecture_on_locked_layer_or_with_invalid_definition()
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
        Assert.Equal(WarehouseMapSaveStatus.Success,
            (await service.InitializeAsync(Guid.NewGuid(), user.Id, "1234", "Inicial")).Status);
        fixture.Db.ChangeTracker.Clear();
        var map = await service.GetAsync(true);
        var geometry = map.Elements.Concat(map.Unplaced).Select(item =>
            new WarehouseMapGeometry(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation,
                item.ZIndex, item.IsVisible)).ToArray();
        var invalid = new WarehouseMapArchitectureItem(Guid.NewGuid(), "ZONES", "Text",
            new string('X', 121), 10, 10, 100, 24, 0, 0, [],
            "HEX-FF0000", "NONE", 20, false, 1, false);

        var result = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", null, geometry,
            LayerStates(map), ArchitectureGeometry(map).Append(invalid).ToArray()));

        Assert.Equal(WarehouseMapSaveStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ValidationErrors, error => error.Contains("compatible", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ValidationErrors, error => error.Contains("estilo", StringComparison.OrdinalIgnoreCase));

        var tooManyPoints = new WarehouseMapArchitectureItem(Guid.NewGuid(), "STRUCTURE", "Polyline", null,
            10, 10, 100, 100, 0, 0,
            Enumerable.Range(0, 65).Select(index => new WarehouseMapPoint(index, index)).ToArray(),
            "SECONDARY", "NONE", 2, false, 1, false);
        var unlockedStructure = LayerStates(map)
            .Select(item => item.Code == "STRUCTURE" ? item with { IsLocked = false } : item).ToArray();
        var excessive = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", null, geometry,
            unlockedStructure, ArchitectureGeometry(map).Append(tooManyPoints).ToArray()));
        Assert.Equal(WarehouseMapSaveStatus.ValidationFailed, excessive.Status);

        var duplicate = ArchitectureGeometry(map).Append(ArchitectureGeometry(map)[0]).ToArray();
        var duplicated = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", null, geometry,
            LayerStates(map), duplicate));
        Assert.Equal(WarehouseMapSaveStatus.ValidationFailed, duplicated.Status);
        Assert.Single(await fixture.Db.WarehouseMapRevisions.ToListAsync());
        Assert.Equal(17, await fixture.Db.WarehouseMapArchitecturalElements.CountAsync());
    }

    [Fact]
    public async Task Warehouse_map_reviews_groups_dimensions_scale_and_reversible_archiving_without_writes()
    {
        await using var fixture = new Fixture();
        var protector = new PinProtector("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
        var pins = new UserPinService(fixture.Db, protector);
        var role = new Role { Id = 1, Code = "ADMIN", Name = "Administrador" };
        var user = new User { FullName = "Map Admin 11.9.3", Role = role, PinLookup = string.Empty, PinHash = string.Empty };
        await pins.AssignAsync(user, "1234");
        fixture.Db.AddRange(role, user, Rack("A", 1, 1));
        await fixture.Db.SaveChangesAsync();
        var service = new WarehouseMapService(fixture.Db, pins, TimeProvider.System);
        Assert.Equal(WarehouseMapSaveStatus.Success,
            (await service.InitializeAsync(Guid.NewGuid(), user.Id, "1234", "Inicial")).Status);
        fixture.Db.ChangeTracker.Clear();

        var map = await service.GetAsync(true);
        var geometry = map.Elements.Concat(map.Unplaced).Select(item =>
            new WarehouseMapGeometry(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation,
                item.ZIndex, item.IsVisible)).ToArray();
        var layers = LayerStates(map).Select(item => item.Code is "ZONES" or "AISLES" or "DIMENSIONS"
            ? item with { IsLocked = false } : item).ToArray();
        var zoneGroup = Guid.NewGuid();
        var dimensionGroup = Guid.NewGuid();
        var zoneOne = Guid.NewGuid();
        var zoneTwo = Guid.NewGuid();
        var dimensionLine = Guid.NewGuid();
        var dimensionText = Guid.NewGuid();
        var aisle = Guid.NewGuid();
        var architecture = ArchitectureGeometry(map).Concat([
            new(zoneOne, "ZONES", "Rectangle", "Grupo 1", 400, 200, 80, 60, 0, 0, [], "WARNING", "WARNING", 2, false, 900, false, zoneGroup),
            new(zoneTwo, "ZONES", "Rectangle", "Grupo 2", 520, 200, 80, 60, 0, 0, [], "WARNING", "WARNING", 2, false, 901, false, zoneGroup),
            new(dimensionLine, "DIMENSIONS", "Polyline", null, 100, 100, 100, 1, 0, 0, [new(0, 0), new(100, 0)], "PRIMARY", "NONE", 2, false, 902, false, dimensionGroup),
            new(dimensionText, "DIMENSIONS", "Text", "10 in", 150, 88, 140, 24, 0, 0, [], "NONE", "PRIMARY", 0, false, 903, false, dimensionGroup),
            new(aisle, "AISLES", "Rectangle", "Pasillo angosto", 800, 500, 100, 100, 0, 0, [], "INFO", "NONE", 2, true, 904, false)
        ]).ToArray();
        var command = new WarehouseMapSaveCommand(Guid.NewGuid(), user.Id, "1234", "Productividad",
            geometry, layers, architecture, 10m, "IMPERIAL");
        var revisionsBeforeReview = await fixture.Db.WarehouseMapRevisions.CountAsync();

        var review = await service.ReviewAsync(command);

        Assert.Empty(review.Errors);
        Assert.Equal(5, review.Summary.Added);
        Assert.True(review.Summary.ScaleChanged);
        Assert.Contains(review.Warnings, warning => warning.Code == "NARROW_AISLE" && warning.ElementIds.Contains(aisle));
        Assert.Equal(revisionsBeforeReview, await fixture.Db.WarehouseMapRevisions.CountAsync());
        var saved = await service.SaveAsync(command);
        Assert.Equal(WarehouseMapSaveStatus.Success, saved.Status);
        Assert.Equal(22, await fixture.Db.WarehouseMapArchitecturalElements.CountAsync());
        Assert.Equal(10m, (await fixture.Db.WarehouseMapLayouts.SingleAsync()).ScaleUnitsPerInch);

        fixture.Db.ChangeTracker.Clear();
        map = await service.GetAsync(true);
        var archived = ArchitectureGeometry(map).Select(item => item.Id == zoneOne || item.Id == zoneTwo
            ? item with { IsArchived = true }
            : item.Id == dimensionText ? item with { Label = "25.4 cm" } : item).ToArray();
        var archivedResult = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", "Archivar grupo",
            geometry, LayerStates(map), archived, 10m, "METRIC"));
        Assert.Equal(WarehouseMapSaveStatus.Success, archivedResult.Status);
        fixture.Db.ChangeTracker.Clear();
        var published = await service.GetAsync(false);
        var editor = await service.GetAsync(true);
        Assert.DoesNotContain(published.Architecture, item => item.Id == zoneOne || item.Id == zoneTwo);
        Assert.Equal(2, editor.ArchivedArchitecture.Count(item => item.GroupId == zoneGroup));
        Assert.Equal(22, await fixture.Db.WarehouseMapArchitecturalElements.CountAsync());
        Assert.Equal(WarehouseMapMeasurementSystem.Metric,
            (await fixture.Db.WarehouseMapLayouts.AsNoTracking().SingleAsync()).MeasurementSystem);
        var revision = await fixture.Db.WarehouseMapRevisions.OrderByDescending(item => item.NewVersion).FirstAsync();
        using var changes = JsonDocument.Parse(revision.ChangesJson);
        Assert.Equal(4, changes.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(2, changes.RootElement.GetProperty("Architecture").GetProperty("Archived").GetArrayLength());
        Assert.Contains(await service.GetRevisionsAsync(), item => item.SchemaVersion == 4
            && item.Summary.Contains("2 archivados", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(28, "IMPERIAL", "28 in")]
    [InlineData(86, "IMPERIAL", "2 yd 14 in")]
    [InlineData(10, "METRIC", "25.4 cm")]
    [InlineData(40, "METRIC", "1.02 m")]
    public void Warehouse_map_formats_imperial_and_metric_distances(decimal inches, string system, string expected) =>
        Assert.Equal(expected, WarehouseMapService.FormatDistance(inches, system));

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

        var result = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", null, geometry,
            LayerStates(map), ArchitectureGeometry(map)));
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

        var result = await service.SaveAsync(new(Guid.NewGuid(), user.Id, "1234", "Ajuste", geometry,
            LayerStates(map), ArchitectureGeometry(map)));

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

    private static WarehouseMapLayerState[] LayerStates(WarehouseMapView map) =>
        map.Layers.Select(item => new WarehouseMapLayerState(item.Code, item.IsLocked)).ToArray();

    private static WarehouseMapArchitectureItem[] ArchitectureGeometry(WarehouseMapView map) =>
        map.Architecture.Concat(map.ArchivedArchitecture).Select(item => new WarehouseMapArchitectureItem(item.Id, item.LayerCode, item.Kind,
            item.Label, item.X, item.Y, item.Width, item.Height, item.Rotation, item.CornerRadius, item.Points,
            item.StrokeToken, item.FillToken, item.StrokeWidth, item.IsDashed, item.ZIndex, item.IsLocked,
            item.GroupId, item.IsArchived)).ToArray();

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
