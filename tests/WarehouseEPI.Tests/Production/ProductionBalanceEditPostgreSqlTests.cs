using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionBalanceEditPostgreSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_and_balance_edits_work_on_isolated_postgresql(bool multipleAreas)
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
            if (multipleAreas) await ProductionDailyFlexibleTests.VerifyBalanceEditProjectionAsync(db);
            else await ProductionDailyFlexibleTests.VerifyBalanceEditsAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
