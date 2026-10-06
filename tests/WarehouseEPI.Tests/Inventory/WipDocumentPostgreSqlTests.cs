using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Tests.Imports;

namespace WarehouseEPI.Tests.Inventory;

public sealed class WipDocumentPostgreSqlTests
{
    [Fact]
    public async Task Migration_cutover_and_concurrent_applications_preserve_warehouse_and_document_limits()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var connection = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var name = "warehouse_epi_wip_document_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_wip_document_test_[a-f0-9]{32}$", name);
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(connection)
            { Database = name, Pooling = false, CommandTimeout = 120 }.ConnectionString).Options;
            await using var db = new WarehouseDbContext(options);
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var (pins, product, source, wip) = await WipDocumentTests.SeedAsync(db);
            db.InventoryBalances.AddRange(new InventoryBalance { ProductId = product.Id, LocationId = source.Id, Quantity = 74 },
                new InventoryBalance { ProductId = product.Id, LocationId = wip.Id, Quantity = 26 });
            await db.SaveChangesAsync();
            var cutover = new WipDocumentCutoverService(db, pins, TimeProvider.System);
            var preview = await cutover.PreviewAsync();
            var command = new WipCutoverCommand(Guid.NewGuid(), preview.Revision, "1234", "Integration cutover");
            var converted = await cutover.ConfirmAsync(command);
            Assert.True(converted.Status == InventoryMovementStatus.Success, string.Join("; ", converted.ValidationErrors));
            Assert.Equal(InventoryMovementStatus.Success, (await cutover.ConfirmAsync(command)).Status);
            Assert.Equal(74, await db.InventoryBalances.Where(x => x.LocationId == source.Id).SumAsync(x => x.Quantity));
            Assert.Equal(0, await db.InventoryBalances.Where(x => x.LocationId == wip.Id).SumAsync(x => x.Quantity));
            var blocked = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE inventory_balances SET quantity = 1 WHERE location_id = {wip.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, blocked.SqlState);
            async Task<InventoryMovementResult> Apply()
            {
                await using var session = new WarehouseDbContext(options);
                return await new WipDocumentService(session, WipTransferImportTests.Pins(session), TimeProvider.System).ConfirmAsync(
                    new(Guid.NewGuid(), product.Id, wip.Id, 20, WipDocumentApplicationKind.Consumption, "1234"));
            }
            var results = await Task.WhenAll(Apply(), Apply());
            Assert.Single(results, x => x.Status == InventoryMovementStatus.Success);
            Assert.Single(results, x => x.Status == InventoryMovementStatus.ValidationFailed);
            Assert.Equal(20, await db.WipDocumentApplications.SumAsync(x => x.Quantity));
            Assert.Equal(74, await db.InventoryBalances.Where(x => x.LocationId == source.Id).SumAsync(x => x.Quantity));
            var clock = new WarehouseEPI.Infrastructure.Settings.WarehouseClock(new WarehouseEPI.Infrastructure.Settings.WarehouseSettingsService(db));
            var report = await new WipReportService(db, clock).GetTrackedPageAsync(new(null, null), 1, 25);
            Assert.Equal(6, Assert.Single(report.Inventory).Quantity);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin) { CommandTimeout = 120 };
            await drop.ExecuteNonQueryAsync();
        }
    }
}
