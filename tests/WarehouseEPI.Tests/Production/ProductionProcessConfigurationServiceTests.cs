using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionProcessConfigurationServiceTests
{
    [Fact]
    public async Task New_process_view_keeps_empty_id_so_post_creates_the_process()
    {
        await using var fixture = await Fixture.CreateAsync();

        var view = await fixture.Service.GetAsync(Guid.Empty);

        Assert.NotNull(view);
        Assert.Equal(Guid.Empty, view.Id);
    }

    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Alert_thresholds_require_reason_and_are_preserved_in_the_audit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var withoutReason = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), Guid.Empty, "COS", "Costura", true, 0,
            [], [], [], null, fixture.Pin, InactivityAlertHours: 12, ReworkAlertHours: 24));
        var saved = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), Guid.Empty, "COS", "Costura", true, 0,
            [], [], [], "Umbrales iniciales", fixture.Pin, InactivityAlertHours: 12, ReworkAlertHours: 24));

        Assert.Equal(ProcessConfigurationStatus.ValidationFailed, withoutReason.Status);
        Assert.Equal(ProcessConfigurationStatus.Success, saved.Status);
        var stage = await fixture.Db.ProductionStages.SingleAsync(x => x.Id == saved.ProcessId);
        Assert.Equal(12, stage.InactivityAlertHours);
        Assert.Equal(24, stage.ReworkAlertHours);
        var revision = await fixture.Db.ProductionProcessRevisions.SingleAsync();
        Assert.Contains("InactivityAlertHours", revision.AfterJson);
        Assert.Contains("Umbrales iniciales", revision.Reason);
    }

    [Fact]
    public async Task Process_can_link_one_area_and_one_complete_wip_rack_without_inventory_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operation = Guid.NewGuid();
        var command = new SaveProcessCommand(operation, Guid.Empty, "cos", "Costura", true, 0,
            [fixture.Area.Id], [], [new("M", 2)], "Asignación inicial", fixture.Pin);
        var result = await fixture.Service.SaveProcessAsync(command);
        var retry = await fixture.Service.SaveProcessAsync(command);
        var conflictingRetry = await fixture.Service.SaveProcessAsync(command with { Name = "Costura modificada" });

        Assert.Equal(ProcessConfigurationStatus.Success, result.Status);
        Assert.Equal(result.ProcessId, retry.ProcessId);
        Assert.Equal(ProcessConfigurationStatus.IdempotencyConflict, conflictingRetry.Status);
        var targets = await fixture.Db.ProductionProcessWipTargets.OrderBy(x => x.RowCode).ToListAsync();
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, x => x.LocationId == fixture.Area.Id);
        Assert.Contains(targets, x => x.RowCode == "M" && x.RackNumber == 2);
        Assert.Empty(await fixture.Db.InventoryMovements.ToListAsync());
        Assert.Single(await fixture.Db.ProductionProcessRevisions.ToListAsync());

        fixture.Db.Locations.Add(new Location { Code = "M-2-2", Kind = LocationKind.Rack, RowCode = "M", RackNumber = 2, PalletNumber = 2, OperationalRole = LocationOperationalRole.Wip });
        await fixture.Db.SaveChangesAsync();
        Assert.Single(await fixture.Service.RackProcessIdsAsync("M", 2));
    }

    [Fact]
    public async Task Process_saves_associations_and_default_together_and_later_reports_invalidation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), Guid.Empty, "COS", "Costura", true, 0,
            [fixture.Area.Id], [], [new("M", 2)], "Configuración inicial con predeterminado", fixture.Pin,
            $"P:{fixture.Area.Id}"));

        Assert.Equal(ProcessConfigurationStatus.Success, created.Status);
        var stage = await fixture.Db.ProductionStages.SingleAsync(x => x.Id == created.ProcessId);
        Assert.Equal(fixture.Area.Id, stage.DefaultWipLocationId);
        var current = await fixture.Service.GetAsync(stage.Id);
        Assert.True(current!.DefaultWipTargetAvailable);
        Assert.Equal(fixture.Area.Code, current.DefaultWipTargetLabel);

        fixture.Area.IsBlocked = true;
        await fixture.Db.SaveChangesAsync();

        var invalidated = await fixture.Service.GetAsync(stage.Id);
        Assert.False(invalidated!.DefaultWipTargetAvailable);
        Assert.Equal($"P:{fixture.Area.Id}", invalidated.DefaultWipTargetKey);
    }

    [Fact]
    public async Task Removing_wip_role_clears_associations_from_each_editor()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), Guid.Empty, "COS", "Costura", true, 0,
            [fixture.Area.Id], [], [new("M", 2)], "Asignación inicial", fixture.Pin));

        var areaResult = await fixture.Service.ApplyAreaAsync(fixture.Area.Id, LocationOperationalRole.Storage, [], 1);
        await fixture.Db.SaveChangesAsync();
        var rackResult = await fixture.Service.ApplyRackAsync("M", 2, LocationOperationalRole.Storage, [], 2);
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(ProcessConfigurationStatus.Success, areaResult.Status);
        Assert.Equal(ProcessConfigurationStatus.Success, rackResult.Status);
        Assert.Empty(await fixture.Service.AreaProcessIdsAsync(fixture.Area.Id));
        Assert.Empty(await fixture.Service.RackProcessIdsAsync("M", 2));
        Assert.NotNull(created.ProcessId);
    }

    [Fact]
    public async Task Stale_editor_and_non_wip_target_are_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), Guid.Empty, "COS", "Costura", true, 0,
            [fixture.Area.Id], [], [], null, fixture.Pin));
        var stale = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), first.ProcessId!.Value, "COS", "Costura", true, 0,
            [], [], [], null, fixture.Pin));
        var invalid = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), Guid.Empty, "SEL", "Sellado", true, 1,
            [fixture.Storage.Id], [], [], null, fixture.Pin));

        Assert.Equal(ProcessConfigurationStatus.ConcurrencyConflict, stale.Status);
        Assert.Equal(ProcessConfigurationStatus.ValidationFailed, invalid.Status);
    }

    [Fact]
    public async Task Active_route_prevents_process_deactivation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), Guid.Empty, "COS", "Costura", true, 0, [], [], [], null, fixture.Pin));
        fixture.Db.ProductionRoutes.Add(new ProductionRoute { ProductId = fixture.Product.Id, Name = "Ruta", Stages =
            [new ProductionRouteStage { StageId = created.ProcessId!.Value, Sequence = 1 }] });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), created.ProcessId.Value, "COS", "Costura", false, 1, [], [], [], null, fixture.Pin));
        Assert.Equal(ProcessConfigurationStatus.ValidationFailed, result.Status);
        Assert.Contains("ruta activa", result.Errors![0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Target_search_returns_available_wip_areas_and_each_complete_rack_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Locations.AddRange(
            new Location { Code = "M-2-2", Kind = LocationKind.Rack, RowCode = "M", RackNumber = 2, PalletNumber = 2, OperationalRole = LocationOperationalRole.Wip },
            new Location { Code = "X-1-1", Kind = LocationKind.Rack, RowCode = "X", RackNumber = 1, PalletNumber = 1, OperationalRole = LocationOperationalRole.Wip },
            new Location { Code = "X-1-2", Kind = LocationKind.Rack, RowCode = "X", RackNumber = 1, PalletNumber = 2, OperationalRole = LocationOperationalRole.Storage },
            new Location { Code = "WIP-INACTIVO", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip, IsActive = false },
            new Location { Code = "Z-3-1", Kind = LocationKind.Rack, RowCode = "Z", RackNumber = 3, PalletNumber = 1, OperationalRole = LocationOperationalRole.Wip, IsActive = false, IsPhysicallyPresent = false });
        await fixture.Db.SaveChangesAsync();

        var rackResults = await fixture.Service.SearchWipTargetsAsync("m-2");
        var areaResults = await fixture.Service.SearchWipTargetsAsync("wip-2");
        var mixedResults = await fixture.Service.SearchWipTargetsAsync("x-1");
        var mixedRowResults = await fixture.Service.SearchWipTargetsAsync("fila x");
        var inactiveResults = await fixture.Service.SearchWipTargetsAsync("inactivo");
        var absentResults = await fixture.Service.SearchWipTargetsAsync("z-3");

        var rack = Assert.Single(rackResults);
        Assert.Equal("R:M:2", rack.Key);
        Assert.Equal("rack", rack.Type);
        Assert.Equal("M-2", rack.Label);
        Assert.Equal("A:" + fixture.Area.Id, Assert.Single(areaResults).Key);
        Assert.Empty(mixedResults);
        var mixedRow = Assert.Single(mixedRowResults);
        Assert.Equal("F:X", mixedRow.Key);
        Assert.Equal("row", mixedRow.Type);
        Assert.Empty(inactiveResults);
        Assert.Empty(absentResults);
    }

    [Fact]
    public async Task Row_target_covers_storage_and_future_racks_and_removes_redundant_direct_target()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Locations.Add(new Location
        {
            Code = "M-3-1", Kind = LocationKind.Rack, RowCode = "M", RackNumber = 3,
            PalletNumber = 1, OperationalRole = LocationOperationalRole.Storage
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), Guid.Empty, "COS", "Costura", true, 0,
            [], ["m"], [new("M", 2)], "Aplica a toda la fila", fixture.Pin));

        Assert.Equal(ProcessConfigurationStatus.Success, result.Status);
        var target = Assert.Single(await fixture.Db.ProductionProcessWipTargets.ToListAsync());
        Assert.Equal("M", target.RowCode);
        Assert.Null(target.RackNumber);
        var revision = Assert.Single(await fixture.Db.ProductionProcessRevisions.ToListAsync());
        Assert.Contains("\"Rows\":[\"M\"]", revision.AfterJson, StringComparison.Ordinal);
        Assert.Empty(await fixture.Service.RackDirectProcessIdsAsync("M", 2));
        Assert.Single(await fixture.Service.RowProcessIdsAsync("M"));
        Assert.Single(await fixture.Service.RackProcessIdsAsync("M", 2));
        Assert.Single(await fixture.Service.RackProcessIdsAsync("M", 3));

        var rackEdit = await fixture.Service.ApplyRackAsync("M", 2, LocationOperationalRole.Wip,
            [result.ProcessId!.Value], 1);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(ProcessConfigurationStatus.Success, rackEdit.Status);
        Assert.Empty(await fixture.Service.RackDirectProcessIdsAsync("M", 2));

        fixture.Db.Locations.Add(new Location
        {
            Code = "M-4-1", Kind = LocationKind.Rack, RowCode = "M", RackNumber = 4,
            PalletNumber = 1, OperationalRole = LocationOperationalRole.Storage
        });
        await fixture.Db.SaveChangesAsync();

        Assert.Single(await fixture.Service.RackProcessIdsAsync("M", 4));
        Assert.Empty(await fixture.Db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Adding_or_removing_a_row_requires_a_reason()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.SaveProcessAsync(new(Guid.NewGuid(), Guid.Empty, "COS", "Costura", true, 0,
            [], ["M"], [], null, fixture.Pin));

        Assert.Equal(ProcessConfigurationStatus.ValidationFailed, result.Status);
        Assert.Contains("filas o racks", result.Errors![0], StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public WarehouseDbContext Db { get; }
        public ProductionProcessConfigurationService Service { get; }
        public Location Area { get; }
        public Location Storage { get; }
        public Product Product { get; }
        public readonly string Pin = "4826";
        private Fixture(WarehouseDbContext db, ProductionProcessConfigurationService service, Location area, Location storage, Product product)
            => (Db, Service, Area, Storage, Product) = (db, service, area, storage, product);

        public static async Task<Fixture> CreateAsync()
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await db.Database.EnsureCreatedAsync();
            var pins = new UserPinService(db, new PinProtector(Key));
            var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "", PinHash = "" };
            await pins.AssignAsync(admin, "4826"); db.Users.Add(admin);
            var area = new Location { Code = "WIP-2", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
            var storage = new Location { Code = "STORAGE", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Storage };
            var rack = new Location { Code = "M-2-1", Kind = LocationKind.Rack, RowCode = "M", RackNumber = 2, PalletNumber = 1, OperationalRole = LocationOperationalRole.Wip };
            var product = new Product { Sku = "PT", BaseUnitId = 1 };
            db.AddRange(area, storage, rack, product); await db.SaveChangesAsync();
            var defaults = new ProductionWipDefaultService(db, pins, TimeProvider.System);
            return new(db, new ProductionProcessConfigurationService(db, pins, TimeProvider.System,
                NullLogger<ProductionProcessConfigurationService>.Instance, defaults), area, storage, product);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
