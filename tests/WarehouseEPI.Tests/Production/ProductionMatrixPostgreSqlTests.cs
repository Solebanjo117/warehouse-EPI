using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

public sealed class ProductionMatrixPostgreSqlTests
{
    [Fact]
    public async Task Failure_in_second_area_rolls_back_first_area_then_retry_commits_once()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var database = "warehouse_epi_matrix_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_matrix_test_[a-f0-9]{32}$", database);
        var adminString = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false }.ConnectionString;
        var testString = new NpgsqlConnectionStringBuilder(source) { Database = database, Pooling = false }.ConnectionString;
        await using var admin = new NpgsqlConnection(adminString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var failure = new FailSecondArea();
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testString).AddInterceptors(failure).Options);
            await db.Database.MigrateAsync();
            var seed = await ProductionDailyFlexibleTests.SeedAsync(db);
            var service = ProductionDailyFlexibleTests.Capture(db);
            var command = new ProductionMatrixCommand(Guid.NewGuid(), seed.Date, seed.Shift,
                Enum.GetValues<ProductionDailyArea>().Select(area => new ProductionMatrixRow(seed.Product.Id, area, 5, "matrix test")).ToArray(), Pin: "4826");
            var preview = await service.PreviewMatrixAsync(command);
            Assert.True(preview.CanConfirm, string.Join(" | ", preview.Errors));
            command = command with { ReviewedFingerprint = preview.Fingerprint };
            var orders = await db.ProductionWorkOrders.AsNoTracking().Select(x => new { x.Id, x.Version }).ToListAsync();
            failure.Enabled = true;
            Assert.False((await service.ConfirmMatrixAsync(command)).Success);
            Assert.True(failure.Triggered);
            Assert.Empty(await db.ProductionDailyCaptures.AsNoTracking().ToListAsync());
            Assert.Empty(await db.ProductionCaptureSubmissions.AsNoTracking().ToListAsync());
            Assert.Equal(orders, await db.ProductionWorkOrders.AsNoTracking().Select(x => new { x.Id, x.Version }).ToListAsync());
            failure.Enabled = false;
            var saved = await service.ConfirmMatrixAsync(command);
            Assert.True(saved.Success, string.Join(" | ", saved.Errors ?? []));
            Assert.True((await service.ConfirmMatrixAsync(command)).Success);
            Assert.Equal(3, await db.ProductionDailyCaptures.CountAsync());
            Assert.Single(await db.ProductionCaptureSubmissions.ToListAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class FailSecondArea : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<ProductionDailyCapture>().Any(x => x.State == EntityState.Added && x.Entity.Area == ProductionDailyArea.Sewing))
            {
                Triggered = true;
                throw new DbUpdateException("Injected failure in the second area.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
