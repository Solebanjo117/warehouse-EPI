using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionImportReplacementPostgreSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_rolls_back_all_weeks_and_draft_on_failure_then_retries_once(bool linked)
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var name = "warehouse_epi_import_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_import_test_[a-f0-9]{32}$", name);
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var testBuilder = new NpgsqlConnectionStringBuilder(source) { Database = name, Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var fault = new FailDraftConfirmation();
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseNpgsql(testBuilder.ConnectionString).AddInterceptors(fault).Options);
            await db.Database.MigrateAsync();
            if (linked)
            {
                await ProductionImportLinkedReplacementTests.VerifyPublishedReplacementAsync(db);
                return;
            }
            var actor = await ProductionOpeningImportTests.Seed(db);
            var bytes = ProductionOpeningImportTests.Bytes();
            var importer = new ProductionScheduleImportService(db, TimeProvider.System);
            var preview = await importer.PreviewAsync(new MemoryStream(bytes), "file.xlsx");
            Assert.True((await importer.ConfirmAsync(preview, Guid.NewGuid(), actor)).Success);
            db.ChangeTracker.Clear();
            var lines = await db.ProductionScheduleLines.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Quantity, x.IsCancelled, x.Version }).ToArrayAsync();
            var captures = await db.ProductionDailyCaptures.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Quantity, x.Status, x.RecordedAt }).ToArrayAsync();
            var service = ProductionOpeningImportTests.Service(db);
            var id = await service.CreateAsync("same-file.xlsx", bytes, actor);
            var draft = (await service.GetAsync(id, actor))!;
            Assert.True((await service.ReviseAsync(id, draft.Version, actor,
                ProductionScheduleImportResolutions.None with { ReplaceProgramming = true })).Success);
            draft = (await service.GetAsync(id, actor))!;
            Assert.True(draft.Preview.CanConfirm);
            var command = new ProductionImportConfirmCommand(id, draft.Version, draft.ReviewedFingerprint, Guid.NewGuid(), actor);
            fault.Armed = true;
            Assert.Equal(ProductionDailyCommandStatus.ConcurrencyConflict, (await service.ConfirmAsync(command)).Status);
            Assert.Equal(lines, await db.ProductionScheduleLines.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Quantity, x.IsCancelled, x.Version }).ToArrayAsync());
            Assert.Equal(ProductionImportDraftStatus.Ready, (await db.ProductionImportDrafts.AsNoTracking().SingleAsync(x => x.Id == id)).Status);
            Assert.Empty(await db.ProductionScheduleRevisions.ToListAsync());
            Assert.True((await service.ConfirmAsync(command)).Success);
            Assert.True((await service.ConfirmAsync(command)).Success);
            Assert.Equal(5, await db.ProductionScheduleRevisions.CountAsync());
            Assert.Equal(captures, await db.ProductionDailyCaptures.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Quantity, x.Status, x.RecordedAt }).ToArrayAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class FailDraftConfirmation : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<ProductionImportDraft>()
                .Any(x => x.Entity.Status == ProductionImportDraftStatus.Confirmed))
            {
                Armed = false;
                throw new DbUpdateException("Injected failure after replacing all weeks.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
