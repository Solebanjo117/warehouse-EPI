using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Reporting;

public sealed class DashboardActivityPostgreSqlTests
{
    [Fact]
    public async Task Aggregation_pagination_and_local_days_translate_in_an_isolated_database()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var connection = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var name = "warehouse_epi_dashboard_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_dashboard_test_[a-f0-9]{32}$", name);
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(connection) { Database = name, Pooling = false }.ConnectionString).Options);
            await db.Database.MigrateAsync();
            var user = new User { FullName = "Dashboard", RoleId = 1, PinHash = "test", PinLookup = "dashboard-test" };
            var location = new Location { Code = "DASH-A", Kind = LocationKind.Area };
            var products = Enumerable.Range(0, 12).Select(i => new Product { Sku = $"DASH-{i:00}", BaseUnitId = 1 }).ToArray();
            db.AddRange(user, location); db.AddRange(products);
            var now = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
            foreach (var product in products)
            {
                var movement = new InventoryMovement
                {
                    OperationId = Guid.NewGuid(),
                    RequestFingerprint = new string('d', 64),
                    Type = InventoryMovementType.Entry,
                    ResponsibleUser = user,
                    OccurredAt = now
                };
                movement.Lines.Add(new() { Product = product, UnitId = 1, Quantity = 1, LineNumber = 1, DestinationLocation = location });
                movement.Lines.Add(new() { Product = product, UnitId = 1, Quantity = 2, LineNumber = 2, DestinationLocation = location });
                db.Add(movement);
            }
            // The autumn DST day contains both 01:30 occurrences and ends at 06:00 UTC the next day.
            foreach (var instant in new[] { "2026-11-01T06:30:00Z", "2026-11-01T07:30:00Z", "2026-11-02T05:59:59Z", "2026-11-02T06:00:00Z" })
            {
                var movement = new InventoryMovement
                {
                    OperationId = Guid.NewGuid(),
                    RequestFingerprint = new string('e', 64),
                    Type = InventoryMovementType.Exit,
                    ResponsibleUser = user,
                    OccurredAt = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)
                };
                movement.Lines.Add(new() { Product = products[0], UnitId = 1, Quantity = 1, LineNumber = 1, SourceLocation = location });
                db.Add(movement);
            }
            await db.SaveChangesAsync();
            var service = new DailyDashboardService(db, new WarehouseSettingsService(db));
            var calendar = await service.GetActivityAsync(now, 90);
            Assert.Equal(90, calendar.Points.Count);
            Assert.Equal(15, calendar.Points[^1].TotalEffectiveOperations);
            Assert.Equal(12, calendar.Points[^1].DistinctSkusCount);
            Assert.Equal(3, calendar.Points[^1].ExitCount);
            Assert.All(calendar.Points.Take(89), point => Assert.Equal(0, point.TotalEffectiveOperations));
            var page = await service.GetActivityProductsAsync(now, 90);
            Assert.Equal(15, page.TotalOperations);
            Assert.Equal(12, page.TotalProducts);
            Assert.Equal(10, page.Items.Count);
            Assert.Equal(4, page.Items[0].Operations);
            Assert.Equal(26.7m, page.Items[0].Percent);
            Assert.Equal(new DashboardLocationDto("DASH-A", 4), Assert.Single(page.Items[0].Locations));
            var second = await service.GetActivityProductsAsync(now, 90, 2);
            Assert.Equal(new[] { "DASH-10", "DASH-11" }, second.Items.Select(item => item.Sku));
            Assert.Equal(16, await db.InventoryMovements.CountAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin) { CommandTimeout = 120 };
            await drop.ExecuteNonQueryAsync();
        }
    }
}
