using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Reporting;

public sealed class WipSummaryPostgreSqlTests
{
    [Fact]
    public async Task Summary_aggregates_on_a_new_isolated_postgresql_database()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var connection = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var name = "warehouse_epi_wip_summary_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_wip_summary_test_[a-f0-9]{32}$", name);
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        // Never reset an existing database. Cleanup runs only after this CREATE succeeds.
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(connection)
                { Database = name, Pooling = false }.ConnectionString).Options;
            await using var db = new WarehouseDbContext(options);
            await db.Database.MigrateAsync();
            await WipSummaryTests.SeedAndVerifyAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
