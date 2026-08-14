using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Inventory;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class PostgreSqlInventoryCollection : ICollectionFixture<PostgreSqlInventoryFixture>
{
    public const string CollectionName = "PostgreSQL inventory";
}

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class PostgreSqlInventoryTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Queries_derive_zero_total_and_minimum_alert_without_a_balance_row()
    {
        var seed = await fixture.SeedAsync("PG-QUERY", "PG-AREA-0", "3100");
        await using var db = fixture.CreateDbContext();
        var product = await db.Products.SingleAsync(item => item.Id == seed.ProductId);
        product.MinimumStock = 3m;
        await db.SaveChangesAsync();
        var queries = new InventoryQueryService(db);

        Assert.Equal(0m, await queries.GetProductTotalAsync(seed.ProductId));
        Assert.Contains(await queries.GetBelowMinimumProductsAsync(), item =>
            item.ProductId == seed.ProductId && item.TotalQuantity == 0m);
    }

    [Fact]
    public async Task Concurrent_entries_do_not_lose_updates()
    {
        var seed = await fixture.SeedAsync("PG-CONCURRENT", "PG-AREA-1", "3101");
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(index =>
            fixture.ConfirmAsync(new(
                Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
                [new(seed.ProductId, 1m, DestinationLocationId: seed.LocationId)]))));

        Assert.All(results, result => Assert.Equal(InventoryMovementStatus.Success, result.Status));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(5m, (await db.InventoryBalances.SingleAsync(balance =>
            balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Quantity);
        Assert.Equal(5, await db.InventoryMovements.CountAsync(movement =>
            movement.Lines.Any(line => line.ProductId == seed.ProductId)));
    }

    [Fact]
    public async Task Concurrent_retries_with_same_uuid_create_one_movement()
    {
        var seed = await fixture.SeedAsync("PG-IDEMPOTENT", "PG-AREA-2", "3102");
        var command = new InventoryMovementCommand(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 2m, DestinationLocationId: seed.LocationId)]);

        var results = await Task.WhenAll(fixture.ConfirmAsync(command), fixture.ConfirmAsync(command));

        Assert.All(results, result => Assert.Equal(InventoryMovementStatus.Success, result.Status));
        Assert.Equal(results[0].MovementId, results[1].MovementId);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.InventoryMovements.CountAsync(movement => movement.OperationId == command.OperationId));
        Assert.Equal(2m, (await db.InventoryBalances.SingleAsync(balance =>
            balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Quantity);
    }

    [Fact]
    public async Task Adjustment_rejects_a_real_postgresql_xmin_change()
    {
        var seed = await fixture.SeedAsync("PG-XMIN", "PG-AREA-3", "3103");
        await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 10m, DestinationLocationId: seed.LocationId)]));
        uint originalVersion;
        await using (var db = fixture.CreateDbContext())
        {
            originalVersion = (await db.InventoryBalances.AsNoTracking().SingleAsync(balance =>
                balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Version;
        }
        await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Entry, seed.Pin,
            [new(seed.ProductId, 1m, DestinationLocationId: seed.LocationId)]));

        var result = await fixture.ConfirmAsync(new(
            Guid.NewGuid(), InventoryMovementType.Adjustment, seed.Pin,
            [new(seed.ProductId, 4m, LocationId: seed.LocationId, ExpectedBalanceVersion: originalVersion)]));

        Assert.Equal(InventoryMovementStatus.BalanceChanged, result.Status);
        await using var verification = fixture.CreateDbContext();
        Assert.Equal(11m, (await verification.InventoryBalances.SingleAsync(balance =>
            balance.ProductId == seed.ProductId && balance.LocationId == seed.LocationId)).Quantity);
    }
}

public sealed class PostgreSqlInventoryFixture : IAsyncLifetime
{
    private const string TestDatabase = "warehouse_epi_test";
    private const string LookupKey =
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private string connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var configuredTest = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION");
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var source = configuredTest ?? configuration.GetConnectionString("Warehouse") ??
            throw new InvalidOperationException(
                "Configure WAREHOUSE_EPI_TEST_CONNECTION o ConnectionStrings:Warehouse en User Secrets.");
        var sourceBuilder = new NpgsqlConnectionStringBuilder(source);
        if (configuredTest is not null && !string.Equals(sourceBuilder.Database, TestDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException($"La conexión de pruebas debe apuntar exclusivamente a {TestDatabase}.");

        var adminBuilder = new NpgsqlConnectionStringBuilder(sourceBuilder.ConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var reset = admin.CreateCommand();
            reset.CommandText = $"DROP DATABASE IF EXISTS {TestDatabase} WITH (FORCE); CREATE DATABASE {TestDatabase};";
            await reset.ExecuteNonQueryAsync();
        }

        sourceBuilder.Database = TestDatabase;
        connectionString = sourceBuilder.ConnectionString;
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    public WarehouseDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    public async Task<InventoryMovementResult> ConfirmAsync(InventoryMovementCommand command)
    {
        await using var db = CreateDbContext();
        var pinService = new UserPinService(db, new PinProtector(LookupKey));
        return await new InventoryMovementService(db, pinService, TimeProvider.System).ConfirmAsync(command);
    }

    public async Task<InventorySeed> SeedAsync(string sku, string locationCode, string pin)
    {
        await using var db = CreateDbContext();
        var pinService = new UserPinService(db, new PinProtector(LookupKey));
        var user = new User
        {
            FullName = $"Operador {sku}",
            RoleId = 2,
            PinLookup = string.Empty,
            PinHash = string.Empty
        };
        Assert.Equal(PinAssignmentResult.Success, await pinService.AssignAsync(user, pin));
        var product = new Product { Sku = sku, BaseUnitId = 1 };
        var location = new Location { Code = locationCode, Kind = LocationKind.Area };
        db.AddRange(user, product, location);
        await db.SaveChangesAsync();
        return new(product.Id, location.Id, pin);
    }
}

public sealed record InventorySeed(Guid ProductId, Guid LocationId, string Pin);
