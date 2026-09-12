using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionWipDefaultServiceTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Saves_one_rule_per_process_with_audited_idempotent_operation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operation = Guid.NewGuid();
        var command = new SaveMaterialWipDefaultsCommand(operation, fixture.Product.Id, 0,
            [new(fixture.Stage.Id, $"P:{fixture.Area.Id}")], "Destino inicial", Fixture.Pin);

        var result = await fixture.Service.SaveMaterialAsync(command);
        var retry = await fixture.Service.SaveMaterialAsync(command);

        Assert.Equal(WipDefaultStatus.Success, result.Status);
        Assert.Equal(WipDefaultStatus.Success, retry.Status);
        var rule = Assert.Single(await fixture.Db.ProductionMaterialWipDefaults.ToListAsync());
        Assert.Equal(fixture.Area.Id, rule.LocationId);
        Assert.Single(await fixture.Db.ProductionMaterialWipRevisions.ToListAsync());
        Assert.Empty(await fixture.Db.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Rejects_duplicate_process_and_incompatible_destination()
    {
        await using var fixture = await Fixture.CreateAsync();
        var duplicate = await fixture.Service.SaveMaterialAsync(new(Guid.NewGuid(), fixture.Product.Id, 0,
            [new(fixture.Stage.Id, $"P:{fixture.Area.Id}"), new(fixture.Stage.Id, $"P:{fixture.Position.Id}")],
            "Duplicada", Fixture.Pin));
        var incompatible = await fixture.Service.SaveMaterialAsync(new(Guid.NewGuid(), fixture.Product.Id, 0,
            [new(fixture.Stage.Id, $"P:{fixture.OtherWip.Id}")], "No asociada", Fixture.Pin));

        Assert.Equal(WipDefaultStatus.ValidationFailed, duplicate.Status);
        Assert.Equal(WipDefaultStatus.ValidationFailed, incompatible.Status);
        Assert.Empty(await fixture.Db.ProductionMaterialWipDefaults.ToListAsync());
    }

    [Fact]
    public async Task Rejects_malformed_destination_without_persisting_a_rule_or_revision()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.SaveMaterialAsync(new(Guid.NewGuid(), fixture.Product.Id, 0,
            [new(fixture.Stage.Id, "destino-manipulado")], "Entrada inválida", Fixture.Pin));

        Assert.Equal(WipDefaultStatus.ValidationFailed, result.Status);
        Assert.Empty(await fixture.Db.ProductionMaterialWipDefaults.ToListAsync());
        Assert.Empty(await fixture.Db.ProductionMaterialWipRevisions.ToListAsync());
    }

    [Fact]
    public async Task Rack_search_reports_partial_availability_and_hides_it_when_every_position_is_blocked()
    {
        await using var fixture = await Fixture.CreateAsync();

        var partial = Assert.Single(await fixture.Service.SearchAsync(fixture.Stage.Id, "M-2"),
            x => x.Key == "R:M:2");
        Assert.Equal("Disponibilidad parcial", partial.Warning);

        fixture.Position.IsBlocked = true;
        await fixture.Db.SaveChangesAsync();

        Assert.DoesNotContain(await fixture.Service.SearchAsync(fixture.Stage.Id, "M-2"), x => x.Key == "R:M:2");
        var unavailable = await fixture.Service.DescribeAsync(fixture.Stage.Id, "R:M:2");
        Assert.False(unavailable.IsAvailable);
    }

    [Fact]
    public async Task Preserves_an_existing_rule_and_reports_when_destination_becomes_blocked()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(WipDefaultStatus.Success, (await fixture.Service.SaveMaterialAsync(new(Guid.NewGuid(),
            fixture.Product.Id, 0, [new(fixture.Stage.Id, $"P:{fixture.Area.Id}")], "Inicial", Fixture.Pin))).Status);
        fixture.Area.IsBlocked = true;
        await fixture.Db.SaveChangesAsync();

        var view = await fixture.Service.GetMaterialAsync(fixture.Product.Id);
        var rule = Assert.Single(view!.Rules);
        Assert.False(rule.IsAvailable);
        Assert.Contains("bloqueada", rule.Warning!, StringComparison.OrdinalIgnoreCase);

        var unchanged = await fixture.Service.SaveMaterialAsync(new(Guid.NewGuid(), fixture.Product.Id, view.Version,
            [new(fixture.Stage.Id, $"P:{fixture.Area.Id}")], "Conservar mientras se corrige", Fixture.Pin));
        Assert.Equal(WipDefaultStatus.Success, unchanged.Status);
    }

    [Fact]
    public async Task Proposed_associations_find_area_rack_and_exact_position_without_rows_as_defaults()
    {
        await using var fixture = await Fixture.CreateAsync();
        var results = await fixture.Service.SearchProposedAsync(
            [$"A:{fixture.Area.Id}", "R:M:2", "F:M"], "M");

        Assert.Contains(results, x => x.Key == "R:M:2" && x.Type == "Rack WIP");
        Assert.Contains(results, x => x.Key == $"P:{fixture.Position.Id}" && x.Type == "Posición WIP");
        Assert.DoesNotContain(results, x => x.Key.StartsWith("F:", StringComparison.Ordinal));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public WarehouseDbContext Db { get; }
        public ProductionWipDefaultService Service { get; }
        public Product Product { get; }
        public ProductionStage Stage { get; }
        public Location Area { get; }
        public Location Position { get; }
        public Location OtherWip { get; }
        public const string Pin = "4826";

        private Fixture(WarehouseDbContext db, ProductionWipDefaultService service, Product product,
            ProductionStage stage, Location area, Location position, Location otherWip) =>
            (Db, Service, Product, Stage, Area, Position, OtherWip) =
            (db, service, product, stage, area, position, otherWip);

        public static async Task<Fixture> CreateAsync()
        {
            var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            await db.Database.EnsureCreatedAsync();
            var pins = new UserPinService(db, new PinProtector(Key));
            var admin = new User { FullName = "Admin", RoleId = 1, PinLookup = "", PinHash = "" };
            await pins.AssignAsync(admin, "4826");
            db.Users.Add(admin);
            var product = new Product { Sku = "MP-1", BaseUnitId = 1 };
            var area = new Location { Code = "WIP-CORTE", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
            var position = new Location { Code = "M-2-1", Kind = LocationKind.Rack, RowCode = "M", RackNumber = 2,
                PalletNumber = 1, OperationalRole = LocationOperationalRole.Wip };
            var second = new Location { Code = "M-2-2", Kind = LocationKind.Rack, RowCode = "M", RackNumber = 2,
                PalletNumber = 2, OperationalRole = LocationOperationalRole.Wip, IsBlocked = true };
            var other = new Location { Code = "WIP-OTRO", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
            var stage = new ProductionStage { Code = "COR", Name = "Corte" };
            stage.WipTargets.Add(new() { Location = area });
            stage.WipTargets.Add(new() { RowCode = "M", RackNumber = 2 });
            db.AddRange(product, area, position, second, other, stage);
            await db.SaveChangesAsync();
            return new(db, new ProductionWipDefaultService(db, pins, TimeProvider.System), product, stage, area, position, other);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class ProductionWipDefaultPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Migration_and_audited_material_default_work_on_isolated_postgresql()
    {
        await using var db = fixture.CreateDbContext();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var admin = new User { FullName = "Admin WIP P1", RoleId = 1, PinLookup = "", PinHash = "" };
        Assert.Equal(PinAssignmentResult.Success, await pins.AssignAsync(admin, "8462"));
        var product = new Product { Sku = $"P1-{Guid.NewGuid():N}".ToUpperInvariant(), BaseUnitId = 1 };
        var area = new Location { Code = $"P1-WIP-{Guid.NewGuid():N}"[..30].ToUpperInvariant(), Kind = LocationKind.Area,
            OperationalRole = LocationOperationalRole.Wip };
        var stage = new ProductionStage { Code = $"P1{Guid.NewGuid():N}"[..20].ToUpperInvariant(), Name = "Proceso P1" };
        stage.WipTargets.Add(new() { Location = area });
        db.AddRange(admin, product, area, stage);
        await db.SaveChangesAsync();
        var version = (await db.ProductionProcessConfigurations.AsNoTracking().SingleAsync()).Version;
        var service = new ProductionWipDefaultService(db, pins, TimeProvider.System);

        var result = await service.SaveMaterialAsync(new(Guid.NewGuid(), product.Id, version,
            [new(stage.Id, $"P:{area.Id}")], "Prueba PostgreSQL P1", "8462"));

        Assert.Equal(WipDefaultStatus.Success, result.Status);
        Assert.True(await db.ProductionMaterialWipDefaults.AnyAsync(x => x.ProductId == product.Id));
        Assert.True(await db.ProductionMaterialWipRevisions.AnyAsync(x => x.ProductId == product.Id));
    }

    [Fact]
    public async Task Product_and_initial_rules_can_roll_back_as_one_postgresql_transaction()
    {
        await using var db = fixture.CreateDbContext();
        var pins = new UserPinService(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
        var admin = new User { FullName = "Admin atómico P1", RoleId = 1, PinLookup = "", PinHash = "" };
        Assert.Equal(PinAssignmentResult.Success, await pins.AssignAsync(admin, "8642"));
        var product = new Product { Sku = $"P1-ATOMIC-{Guid.NewGuid():N}".ToUpperInvariant(), BaseUnitId = 1 };
        var unrelatedArea = new Location { Code = $"P1-X-{Guid.NewGuid():N}"[..30].ToUpperInvariant(), Kind = LocationKind.Area,
            OperationalRole = LocationOperationalRole.Wip };
        var stage = new ProductionStage { Code = $"P1A{Guid.NewGuid():N}"[..20].ToUpperInvariant(), Name = "Proceso atómico P1" };
        db.AddRange(admin, product, unrelatedArea, stage);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();
        var version = (await db.ProductionProcessConfigurations.AsNoTracking().SingleAsync()).Version;
        var service = new ProductionWipDefaultService(db, pins, TimeProvider.System);

        var result = await service.SaveMaterialAsync(new(Guid.NewGuid(), product.Id, version,
            [new(stage.Id, $"P:{unrelatedArea.Id}")], "Regla inicial inválida", "8642"));
        Assert.Equal(WipDefaultStatus.ValidationFailed, result.Status);
        await transaction.RollbackAsync();
        db.ChangeTracker.Clear();

        Assert.False(await db.Products.AnyAsync(x => x.Id == product.Id));
        Assert.False(await db.ProductionMaterialWipRevisions.AnyAsync(x => x.ProductId == product.Id));
    }
}
