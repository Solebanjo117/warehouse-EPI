using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionScheduleCarryoverPostgreSqlTests
{
    [Fact]
    public Task Open_copy_rolls_back_all_rows_orders_and_openings_on_isolated_postgresql() =>
        WithDatabaseAsync(db => ProductionScheduleCarryoverCopyTests.VerifyOpenAsync(db, true));

    [Fact]
    public Task Concurrent_destinations_cannot_reserve_the_same_source_pending_twice() => WithDatabaseAsync(async db =>
    {
        await ProductionScheduleCarryoverCopyTests.VerifyOpenAsync(db);
        var service = ProductionScheduleCarryoverCopyTests.Schedule(db);
        var actor = await db.Users.Where(x => x.FullName == "Copy admin").Select(x => x.Id).SingleAsync();
        var sourceWeek = await db.ProductionScheduleWeeks.OrderByDescending(x => x.WeekStart).FirstAsync();
        var commands = new List<SaveProductionScheduleDraftCommand>();
        for (var offset = 1; offset <= 2; offset++)
        {
            var targetId = (await service.CreateWeekAsync(new(Guid.NewGuid(), sourceWeek.WeekStart.AddDays(7 * offset), actor))).Id!.Value;
            var target = (await service.GetWeekAsync(targetId))!;
            Assert.True((await service.PublishAsync(new(Guid.NewGuid(), targetId, target.Version, "4826", actor))).Success);
            target = (await service.GetWeekAsync(targetId))!;
            var openings = (await new ProductionWeekOpeningService(db).OptionsAsync(targetId))
                .Where(x => x.SourceWeekId == sourceWeek.Id && x.Area == ProductionDailyArea.Sewing).ToArray();
            Assert.Equal(149, openings.Sum(x => x.Available));
            var command = new SaveProductionScheduleDraftCommand(Guid.NewGuid(), targetId, target.Version, [], actor,
                openings.Select(x => new ProductionOpeningChange(x.SourceWeekId, x.SourceLineId, x.Area, x.Available, x.Fingerprint)).ToArray(),
                "Continuar pendientes en el destino elegido");
            var review = await service.PreviewWorkspaceChangesAsync(command);
            Assert.True(review.CanConfirm, string.Join(" | ", review.Errors));
            commands.Add(command with { ReviewedFingerprint = review.Fingerprint });
        }
        var contextOptions = new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(db.Database.GetConnectionString()).Options;
        await using var first = new WarehouseDbContext(contextOptions);
        await using var second = new WarehouseDbContext(contextOptions);
        var results = await Task.WhenAll(
            ProductionScheduleCarryoverCopyTests.Schedule(first).SaveWorkspaceChangesAsync(commands[0], "4826"),
            ProductionScheduleCarryoverCopyTests.Schedule(second).SaveWorkspaceChangesAsync(commands[1], "4826"));
        Assert.Single(results, x => x.Success);
        Assert.Single(results, x => !x.Success);
        Assert.Equal(149, await db.ProductionWeekOpenings.Where(x => x.SourceWeekId == sourceWeek.Id && x.Area == ProductionDailyArea.Sewing)
            .SumAsync(x => x.Quantity));
        Assert.Equal(1, await db.ProductionScheduleRevisions.CountAsync(x => commands.Select(c => c.OperationId).Contains(x.OperationId)));
    });

    internal static async Task WithDatabaseAsync(Func<WarehouseDbContext, Task> verify)
    {
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Configure an isolated PostgreSQL test connection.");
        var name = "warehouse_epi_copy_test_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var testBuilder = new NpgsqlConnectionStringBuilder(source) { Database = name, Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testBuilder.ConnectionString).Options);
            await db.Database.MigrateAsync();
            await verify(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
