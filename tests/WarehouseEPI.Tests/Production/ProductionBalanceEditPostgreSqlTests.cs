using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionBalanceEditPostgreSqlTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Migration_and_balance_edits_work_on_isolated_postgresql(int scenario)
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var database = "warehouse_epi_balance_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_balance_test_[a-f0-9]{32}$", database);
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var testBuilder = new NpgsqlConnectionStringBuilder(source) { Database = database, Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseNpgsql(testBuilder.ConnectionString).Options);
            await db.Database.MigrateAsync();
            if (scenario == 6) await ProductionExplicitCarryoverTests.VerifyMigrationAsync(db);
            else if (scenario == 5) await ProductionExplicitCarryoverTests.VerifyAsync(db);
            else if (scenario >= 3) await ProductionDailyFlexibleTests.VerifyNewBalancePlansAsync(db, scenario == 3 ? 0 : 6, true);
            else if (scenario == 2) await ProductionDailyFlexibleTests.VerifyCombinedBalanceEditsAsync(db);
            else if (scenario == 1) await ProductionDailyFlexibleTests.VerifyBalanceEditProjectionAsync(db);
            else await ProductionDailyFlexibleTests.VerifyBalanceEditsAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
