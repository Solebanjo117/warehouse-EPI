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
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
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
            if (scenario == 9) await VerifyConcurrentConfirmationsAsync(db, testBuilder.ConnectionString);
            else if (scenario is 7 or 8) await ProductionBalanceOptimizationTests.VerifyTargetedReadsAsync(db, scenario == 8);
            else if (scenario == 6) await ProductionExplicitCarryoverTests.VerifyMigrationAsync(db);
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

    private static async Task VerifyConcurrentConfirmationsAsync(WarehouseDbContext db, string connection)
    {
        var seed = await ProductionDailyFlexibleTests.SeedAsync(db);
        var first = new WarehouseEPI.Infrastructure.Production.ProductionBalanceEditCommand(Guid.NewGuid(), seed.Week.Id, seed.Date,
            [new(seed.Product.Id, WarehouseEPI.Core.Entities.ProductionDailyArea.Cutting, 1, 0, 5)], Pin: "4826");
        var second = first with { OperationId = Guid.NewGuid() };
        var service = ProductionDailyFlexibleTests.Capture(db);
        first = first with { ReviewedFingerprint = (await service.PreviewBalanceEditAsync(first)).Fingerprint };
        second = second with { ReviewedFingerprint = (await service.PreviewBalanceEditAsync(second)).Fingerprint };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(connection).Options;
        await using var firstDb = new WarehouseDbContext(options);
        await using var secondDb = new WarehouseDbContext(options);
        var results = await Task.WhenAll(ProductionDailyFlexibleTests.Capture(firstDb).ConfirmBalanceEditAsync(first),
            ProductionDailyFlexibleTests.Capture(secondDb).ConfirmBalanceEditAsync(second));
        Assert.Single(results, x => x.Success);
        Assert.Equal(1, await db.ProductionDailyCaptures.CountAsync());
        Assert.Equal(5, await db.ProductionDailyCaptures.SumAsync(x => x.Quantity));
        Assert.Equal(1, await db.Set<WarehouseEPI.Core.Entities.ProductionBalanceEdit>().CountAsync());
    }
}
