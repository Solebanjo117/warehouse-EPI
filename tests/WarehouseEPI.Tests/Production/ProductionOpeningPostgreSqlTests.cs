using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionOpeningPostgreSqlTests
{
    [Fact]
    public async Task Temporary_database_migration_restart_concurrency_rollback_and_confirmation()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var database = "warehouse_epi_opening_test_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var testBuilder = new NpgsqlConnectionStringBuilder(source) { Database = database, Pooling = false };
        // Only a newly generated, isolated test database is ever created or dropped.
        Assert.StartsWith("warehouse_epi_opening_test_", database, StringComparison.Ordinal);
        Assert.Matches("^warehouse_epi_opening_test_[a-f0-9]{32}$", database);
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await command.ExecuteNonQueryAsync();
        try
        {
            WarehouseDbContext Context(bool fail = false)
            {
                var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testBuilder.ConnectionString);
                if (fail) options.AddInterceptors(new FailFinalSave());
                return new(options.Options);
            }
            Guid actor, id;
            await using (var db = Context())
            {
                await db.Database.MigrateAsync();
                actor = await ProductionOpeningImportTests.Seed(db);
                id = await ProductionOpeningImportTests.Service(db).CreateAsync("file.xlsx", ProductionOpeningImportTests.Bytes(), actor);
            }
            ProductionImportDraftView view;
            await using (var db = Context())
            {
                view = (await ProductionOpeningImportTests.Service(db).GetAsync(id, actor))!;
                Assert.True(view.IsCurrent); Assert.True(view.Preview.CanConfirm);
            }
            async Task<ProductionDailyCommandResult> Revise()
            {
                await using var db = Context();
                return await ProductionOpeningImportTests.Service(db).ReviseAsync(id, view.Version, actor, view.Resolutions);
            }
            var revisions = await Task.WhenAll(Revise(), Revise());
            Assert.Single(revisions, x => x.Success);
            await using (var db = Context()) view = (await ProductionOpeningImportTests.Service(db).GetAsync(id, actor))!;
            var confirm = new ProductionImportConfirmCommand(id, view.Version, view.ReviewedFingerprint, Guid.NewGuid(), actor);
            await using (var db = Context(true))
                await Assert.ThrowsAsync<IOException>(() => ProductionOpeningImportTests.Service(db).ConfirmAsync(confirm));
            await using (var db = Context())
            {
                Assert.Empty(await db.ProductionScheduleWeeks.ToListAsync());
                Assert.Empty(await db.ProductionScheduleImportBatches.ToListAsync());
                Assert.Equal(view.Version, (await db.ProductionImportDrafts.SingleAsync()).Version);
            }
            async Task<ProductionDailyCommandResult> Confirm()
            {
                await using var db = Context(); return await ProductionOpeningImportTests.Service(db).ConfirmAsync(confirm);
            }
            var results = await Task.WhenAll(Confirm(), Confirm());
            Assert.Contains(results, x => x.Success);
            Assert.True((await Confirm()).Success);
            await using (var db = Context())
            {
                Assert.Single(await db.ProductionScheduleImportBatches.ToListAsync());
                Assert.Equal(view.Preview.FinalLineCount, await db.ProductionScheduleLines.CountAsync());
                Assert.Equal(ProductionImportDraftStatus.Confirmed, (await db.ProductionImportDrafts.SingleAsync()).Status);
                Assert.Empty(await db.InventoryMovements.ToListAsync());
                Assert.Empty(await db.ProductionWorkOrders.ToListAsync());
            }
            Guid draft;
            await using (var db = Context())
            {
                var service = ProductionOpeningImportTests.Service(db);
                draft = await service.CreateAsync("draft.xlsx", ProductionOpeningImportTests.Bytes(), actor);
                var pending = (await service.GetAsync(draft, actor))!;
                Assert.True((await service.ReviseAsync(draft, pending.Version, actor, pending.Resolutions)).Success);
                Assert.False((await service.DeleteAsync(id, actor)).Success);
            }
            async Task<ProductionDailyCommandResult> Delete()
            {
                await using var db = Context(); return await ProductionOpeningImportTests.Service(db).DeleteAsync(draft, actor);
            }
            Assert.Single(await Task.WhenAll(Delete(), Delete()), x => x.Success);
            await using (var db = Context())
            {
                Assert.Equal(id, (await db.ProductionImportDrafts.SingleAsync()).Id);
                Assert.DoesNotContain(await db.ProductionImportRevisions.Select(x => x.DraftId).ToListAsync(), x => x == draft);
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class FailFinalSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ProductionImportRevision>().Any(x => x.State == EntityState.Added && x.Entity.Action == "Confirmed"))
                throw new IOException("Simulated failure before the final draft save.");
            return ValueTask.FromResult(result);
        }
    }
}
