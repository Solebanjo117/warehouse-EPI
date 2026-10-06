using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionScheduleProgressPostgreSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Projection_and_open_group_are_valid_on_isolated_postgresql(bool openGroup)
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var name = "warehouse_epi_schedule_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_schedule_test_[a-f0-9]{32}$", name);
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var testBuilder = new NpgsqlConnectionStringBuilder(source) { Database = name, Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testBuilder.ConnectionString).Options);
            await db.Database.MigrateAsync();
            if (openGroup)
            {
                await ProductionScheduleProgressTests.VerifyOpenAsync(db, true);
                var service = ProductionScheduleProgressTests.Schedule(db);
                var saved = await db.ProductionScheduleWeeks.AsNoTracking().SingleAsync();
                Assert.True((await service.ReopenAsync(new(Guid.NewGuid(), saved.Id, saved.Version, saved.CreatedByUserId))).Success);
                var week = (await service.GetWeekAsync(saved.Id))!;
                var productId = Assert.Single(week.Lines).ProductId;
                var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), saved.Id, week.Version,
                    [new("add", null, null, new(week.WeekStart.AddDays(2), productId, 5, null, null, null, null))], saved.CreatedByUserId);
                var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testBuilder.ConnectionString).Options;
                await using var first = new WarehouseDbContext(options);
                await using var second = new WarehouseDbContext(options);
                var results = await Task.WhenAll(ProductionScheduleProgressTests.Schedule(first).SaveWorkspaceChangesAsync(command, "4826"),
                    ProductionScheduleProgressTests.Schedule(second).SaveWorkspaceChangesAsync(command with { OperationId = Guid.NewGuid() }, "4826"));
                Assert.Single(results, result => result.Success);
                Assert.Single(results, result => result.Status == ProductionDailyCommandStatus.ConcurrencyConflict);
                Assert.Equal(2, (await service.GetWeekAsync(saved.Id))!.Lines.Count);
            }
            else await ProductionScheduleProgressTests.VerifyProjectionAsync(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin) { CommandTimeout = 120 };
            await drop.ExecuteNonQueryAsync();
        }
    }
}
