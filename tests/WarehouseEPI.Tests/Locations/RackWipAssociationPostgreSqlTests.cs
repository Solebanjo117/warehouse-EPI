using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Tests.Imports;

namespace WarehouseEPI.Tests.Locations;

public sealed class RackWipAssociationPostgreSqlTests
{
    [Fact]
    public async Task Migration_persistence_and_simultaneous_edits_preserve_one_association()
    {
        var connection = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Configure an isolated PostgreSQL test connection.");
        var name = "warehouse_epi_rack_wip_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(connection)
            { Database = name, Pooling = false }.ConnectionString).Options;
            await using var db = new WarehouseDbContext(options);
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var (user, area, other, _, _) = await RackWipAssociationTests.SeedAsync(db);
            async Task<LocationRackSaveResult> Save(Guid areaId)
            {
                await using var session = new WarehouseDbContext(options);
                return await new LocationRackAdministrationService(session, WipTransferImportTests.Pins(session), TimeProvider.System)
                    .SaveAsync(RackWipAssociationTests.Command(user.Id, areaId));
            }
            var results = await Task.WhenAll(Save(area.Id), Save(other.Id));
            Assert.Single(results, x => x.Status == LocationRackSaveStatus.Success);
            Assert.Single(results, x => x.Status == LocationRackSaveStatus.ValidationFailed);
            Assert.Single(await db.LocationRackWipAssociations.AsNoTracking().ToListAsync());
            Assert.NotNull(await LocationRackWipAssociations.GetAsync(db, "M", 5));
            var associatedId = (await db.LocationRackWipAssociations.SingleAsync()).WipAreaId;
            Assert.Single(await LocationRackWipAssociations.Query(db).Where(x => x.WipAreaId == associatedId)
                .OrderBy(x => x.RowCode).ThenBy(x => x.RackNumber).ToListAsync());
            Assert.Equal(42, await db.InventoryBalances.SumAsync(x => x.Quantity));
            Assert.Empty(await db.InventoryMovements.ToListAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin) { CommandTimeout = 120 };
            await drop.ExecuteNonQueryAsync();
        }
    }
}
