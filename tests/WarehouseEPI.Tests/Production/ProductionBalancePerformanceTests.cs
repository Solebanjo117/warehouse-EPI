using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Tests.Production;

// Select with the PostgreSQLPerformance test filter. Measurements use temporary databases only.
public sealed class ProductionBalancePerformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    [Fact]
    [Trait("Category", "PostgreSQLPerformance")]
    public async Task Measure_isolated_postgresql_balance_review_and_confirmation()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var permission = new NpgsqlCommand("SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user", admin))
            Assert.True((bool)(await permission.ExecuteScalarAsync())!, "Isolated PostgreSQL measurement unavailable: the test role cannot create a database. No operational data was changed.");
        var results = new List<object>();
        foreach (var explicitWeek in new[] { false, true })
        foreach (var count in new[] { 1, 10, 25, 100 })
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var database = "warehouse_epi_perf_test_" + Guid.NewGuid().ToString("N");
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
            try
            {
                var metrics = new Metrics();
                var saves = new Saves();
                var measuring = new AsyncLocal<bool>();
                var phases = new Dictionary<string, double>();
                using var listener = new ActivityListener
                {
                    ShouldListenTo = activitySource => activitySource.Name == "WarehouseEPI.Production.Balance",
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                    ActivityStopped = activity =>
                    {
                        if (measuring.Value) phases[activity.OperationName] = phases.GetValueOrDefault(activity.OperationName) + activity.Duration.TotalMilliseconds;
                    }
                };
                ActivitySource.AddActivityListener(listener);
                var connection = new NpgsqlConnectionStringBuilder(source) { Database = database, Pooling = false };
                await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                    .UseNpgsql(connection.ConnectionString).AddInterceptors(metrics, saves).Options);
                await db.Database.MigrateAsync();
                var setup = await ProductionDailyFlexibleTests.SeedAsync(db);
                setup.Week.ExplicitCarryover = explicitWeek;
                // Two turns per product, plus unrelated population to expose catalog-wide validation.
                var products = new List<Product> { setup.Product };
                for (var index = 1; index < 70; index++)
                {
                    var product = new Product { Sku = $"PERF-{index:D3}", BaseUnitId = setup.Product.BaseUnitId };
                    db.Products.Add(product); products.Add(product);
                    db.ProductionScheduleLines.Add(new() { WeekId = setup.Week.Id, ProductId = product.Id,
                        PlannedDate = setup.Date, Quantity = 100, Sequence = index + 1 });
                }
                await db.SaveChangesAsync();
                var cells = Enumerable.Range(0, count).Select(index => new ProductionBalanceCell(
                    products[index / 2].Id, ProductionDailyArea.Sewing, index % 2 + 1, 0, 1)).ToArray();
                var command = new ProductionBalanceEditCommand(Guid.NewGuid(), setup.Week.Id, setup.Date, cells, Pin: "4826");
                var service = ProductionDailyFlexibleTests.Capture(db);
                metrics.Reset(); saves.Count = 0;
                measuring.Value = true;
                var timer = Stopwatch.StartNew();
                var preview = await service.PreviewBalanceEditAsync(command);
                timer.Stop();
                Assert.True(preview.CanConfirm, string.Join(" | ", preview.Errors));
                var review = new { milliseconds = timer.Elapsed.TotalMilliseconds, metrics.Commands, metrics.SqlMilliseconds, saves.Count,
                    phases = phases.ToDictionary(), sql = metrics.Sql.ToDictionary() };
                Assert.Equal(0, saves.Count);
                metrics.Reset(); saves.Count = 0; phases.Clear(); timer.Restart();
                var confirmed = await service.ConfirmBalanceEditAsync(command with { ReviewedFingerprint = preview.Fingerprint });
                timer.Stop();
                measuring.Value = false;
                Assert.True(confirmed.Success, string.Join(" | ", confirmed.Errors ?? []));
                if (iteration > 0) results.Add(new { explicitWeek, cells = count, iteration, review,
                    confirmation = new { milliseconds = timer.Elapsed.TotalMilliseconds, metrics.Commands, metrics.SqlMilliseconds, saves.Count,
                        phases = phases.ToDictionary(), sql = metrics.Sql.ToDictionary() } });
                Assert.Equal(count, await db.ProductionDailyCaptures.CountAsync());
            }
            finally
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
        var output = Environment.GetEnvironmentVariable("WAREHOUSE_BALANCE_PERFORMANCE_OUTPUT")
            ?? Path.Combine("artifacts", "balance-performance", "results.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, JsonOptions));
    }

    private sealed class Saves : SaveChangesInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
    }

    private sealed class Metrics : DbCommandInterceptor
    {
        public int Commands;
        public double SqlMilliseconds;
        public Dictionary<string, int> Sql { get; } = [];
        public void Reset() { Commands = 0; SqlMilliseconds = 0; Sql.Clear(); }
        private void Record(DbCommand command, CommandExecutedEventData data)
        {
            Commands++; SqlMilliseconds += data.Duration.TotalMilliseconds;
            // Command text only: never log parameter values, connection strings or credentials.
            Sql[command.CommandText] = Sql.GetValueOrDefault(command.CommandText) + 1;
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        { Record(command, eventData); return ValueTask.FromResult(result); }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        { Record(command, eventData); return ValueTask.FromResult(result); }
        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            object? result, CancellationToken cancellationToken = default)
        { Record(command, eventData); return ValueTask.FromResult(result); }
    }
}
