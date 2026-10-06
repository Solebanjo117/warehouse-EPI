using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Imports;

public sealed class WipTransferImportPostgreSqlTests
{
    [Fact]
    public async Task Import_updates_both_stocks_preserves_dates_rolls_back_failure_and_deduplicates_retries()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var connection = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure a PostgreSQL test connection.");
        var name = "warehouse_epi_wip_import_test_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^warehouse_epi_wip_import_test_[a-f0-9]{32}$", name);
        var adminConnection = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false, CommandTimeout = 120 };
        var testConnection = new NpgsqlConnectionStringBuilder(connection) { Database = name, Pooling = false, CommandTimeout = 120 };
        await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            var fault = new FailSecondIssue();
            var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testConnection.ConnectionString).AddInterceptors(fault).Options;
            await using var db = new WarehouseDbContext(options);
            await db.Database.MigrateAsync();
            var pins = WipTransferImportTests.Pins(db);
            var user = new User { FullName = "WIP importer", RoleId = 1, PinLookup = "", PinHash = "" };
            Assert.Equal(PinAssignmentResult.Success, await pins.AssignAsync(user, "1234"));
            var product = new Product { Sku = "PART", BaseUnitId = 1 };
            var rack = new Location { Code = "Z-1-1", RowCode = "Z", RackNumber = 1, PalletNumber = 1, Kind = LocationKind.Rack };
            var wip = new Location { Code = "WIP-A", Description = "WIP A", Kind = LocationKind.Area, OperationalRole = LocationOperationalRole.Wip };
            db.Users.Add(user); db.Products.Add(product); db.Locations.AddRange(rack, wip);
            await db.SaveChangesAsync();
            var movements = new InventoryMovementService(db, pins, TimeProvider.System);
            Assert.Equal(InventoryMovementStatus.Success, (await movements.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry,
                "1234", [new(product.Id, 30m, DestinationLocationId: rack.Id)]))).Status);
            using var stream = WipTransferImportTests.Workbook(("PART", 4m, "WIP A"), ("PART", 3m, "WIP A"));
            var file = WipTransferSpreadsheetReader.Read(stream, "report.xlsx");
            var resolutions = file.Rows.ToDictionary(x => x.Number, _ => new WipTransferResolution(rack.Id, wip.Id));
            using var logs = LoggerFactory.Create(builder => builder.AddConsole());
            var service = WipTransferImportTests.Service(db, logs.CreateLogger<WipTransferImportService>());
            Assert.False((await service.ConfirmAsync(file, resolutions, "0000", false, false)).Success);
            fault.Armed = true;
            var failed = await service.ConfirmAsync(file, resolutions, "1234", false, false);
            Assert.False(failed.Success);
            Assert.Equal(30m, await db.InventoryBalances.Where(x => x.LocationId == rack.Id).SumAsync(x => x.Quantity));
            Assert.Equal(0, await db.InventoryMovements.CountAsync(x => x.Purpose == InventoryMovementPurpose.ProductionIssue));
            var imported = await service.ConfirmAsync(file, resolutions, "1234", false, false);
            Assert.True(imported.Success, imported.Error);
            Assert.Equal(2, imported.Imported);
            Assert.Equal(23m, await db.InventoryBalances.Where(x => x.LocationId == rack.Id).SumAsync(x => x.Quantity));
            Assert.Equal(0m, await db.InventoryBalances.Where(x => x.LocationId == wip.Id).SumAsync(x => x.Quantity));
            Assert.Equal(7m, await db.WipDocuments.Where(x => x.WipLocationId == wip.Id).SumAsync(x => x.Quantity));
            var issues = await db.InventoryMovements.Where(x => x.Purpose == InventoryMovementPurpose.ProductionIssue).ToListAsync();
            Assert.All(issues, x =>
            {
                Assert.Equal(new DateTimeOffset(2026, 9, 8, 5, 0, 0, TimeSpan.Zero), x.OccurredAt);
                Assert.True(x.RecordedAt > x.OccurredAt);
                Assert.Contains(file.Hash, x.Notes);
            });
            // New service/context simulates a restart. Resolutions do not affect durable source IDs.
            await using var retryDb = new WarehouseDbContext(options);
            var retry = await WipTransferImportTests.Service(retryDb).ConfirmAsync(file, resolutions, "1234", false, false);
            Assert.True(retry.Success, retry.Error);
            Assert.Equal(0, retry.Imported);
            Assert.Equal(2, retry.AlreadyImported);
            var reopened = await WipTransferImportTests.Service(retryDb).ReviewAsync(file,
                new Dictionary<int, WipTransferResolution>(), suggestLocations: true);
            Assert.All(reopened.Rows, row =>
            {
                Assert.False(row.Pending);
                Assert.Equal(rack.Code, row.RecordedSource);
                Assert.Equal(wip.Code, row.RecordedDestination);
                Assert.NotNull(row.DocumentId);
                Assert.NotNull(row.RecordedAt);
            });
            Assert.Empty(reopened.Effects);
            using var overlap = WipTransferImportTests.Workbook(("PART", 3m, "WIP A"), ("PART", 4m, "WIP A"), ("PART", 6m, "WIP A"));
            var more = WipTransferSpreadsheetReader.Read(overlap, "renamed.xlsx");
            var moreResult = await WipTransferImportTests.Service(retryDb).ConfirmAsync(more,
                more.Rows.ToDictionary(x => x.Number, _ => new WipTransferResolution(rack.Id, wip.Id)), "1234", false, false);
            Assert.True(moreResult.Success, moreResult.Error);
            Assert.Equal(1, moreResult.Imported);
            Assert.Equal(17m, await retryDb.InventoryBalances.Where(x => x.LocationId == rack.Id).SumAsync(x => x.Quantity));
            Assert.Equal(0m, await retryDb.InventoryBalances.Where(x => x.LocationId == wip.Id).SumAsync(x => x.Quantity));
            Assert.Equal(13m, await retryDb.WipDocuments.Where(x => x.WipLocationId == wip.Id).SumAsync(x => x.Quantity));

            using var concurrentStream = WipTransferImportTests.Workbook(("PART", 2m, "WIP A"));
            var concurrentFile = WipTransferSpreadsheetReader.Read(concurrentStream, "overlap.xlsx");
            async Task<WipTransferImportResult> ConcurrentImportAsync()
            {
                await using var connectionDb = new WarehouseDbContext(options);
                return await WipTransferImportTests.Service(connectionDb).ConfirmAsync(concurrentFile,
                    new Dictionary<int, WipTransferResolution> { [2] = new(rack.Id, wip.Id) }, "1234", false, false);
            }
            var simultaneous = await Task.WhenAll(ConcurrentImportAsync(), ConcurrentImportAsync());
            Assert.All(simultaneous, result => Assert.True(result.Success, result.Error));
            Assert.Equal(1, simultaneous.Sum(x => x.Imported));
            Assert.Equal(1, simultaneous.Sum(x => x.AlreadyImported));
            Assert.Equal(15m, await retryDb.InventoryBalances.Where(x => x.LocationId == rack.Id).SumAsync(x => x.Quantity));
            Assert.Equal(0m, await retryDb.InventoryBalances.Where(x => x.LocationId == wip.Id).SumAsync(x => x.Quantity));
            Assert.Equal(15m, await retryDb.WipDocuments.Where(x => x.WipLocationId == wip.Id).SumAsync(x => x.Quantity));

            using var partialStream = WipTransferImportTests.Workbook(("PART", 1m, "WIP A"), ("MISSING", 99m, "Unknown"), ("PART", 5m, "WIP A"));
            var partialFile = WipTransferSpreadsheetReader.Read(partialStream, "partial.xlsx");
            var partialResolutions = partialFile.Rows.ToDictionary(x => x.Number, _ => new WipTransferResolution(rack.Id, wip.Id));
            // A new request reloads balances changed by the concurrent import contexts.
            retryDb.ChangeTracker.Clear();
            var partialService = WipTransferImportTests.Service(retryDb);
            var oneDelivery = await partialService.ConfirmAsync(WipTransferSelection.Select(partialFile, "PART", 4), partialResolutions, "1234", false, false);
            Assert.True(oneDelivery.Success, oneDelivery.Error);
            Assert.Equal(1, oneDelivery.Imported);
            Assert.True(await retryDb.InventoryMovements.AnyAsync(x => x.OperationId == partialFile.Rows[2].OperationId));
            Assert.False(await retryDb.InventoryMovements.AnyAsync(x => x.OperationId == partialFile.Rows[0].OperationId));
            var selectedProduct = await partialService.ConfirmAsync(WipTransferSelection.Select(partialFile, "PART"), partialResolutions, "1234", false, false);
            Assert.True(selectedProduct.Success, selectedProduct.Error);
            Assert.Equal(1, selectedProduct.Imported);
            Assert.Equal(1, selectedProduct.AlreadyImported);
            Assert.False(await retryDb.InventoryMovements.AnyAsync(x => x.OperationId == partialFile.Rows[1].OperationId));
            Assert.Equal(9m, await retryDb.InventoryBalances.Where(x => x.LocationId == rack.Id).SumAsync(x => x.Quantity));
            Assert.Equal(0m, await retryDb.InventoryBalances.Where(x => x.LocationId == wip.Id).SumAsync(x => x.Quantity));
            Assert.Equal(21m, await retryDb.WipDocuments.Where(x => x.WipLocationId == wip.Id).SumAsync(x => x.Quantity));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin) { CommandTimeout = 120 };
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class FailSecondIssue : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<InventoryMovement>().Count(x => x.Entity.Purpose == InventoryMovementPurpose.ProductionIssue) >= 2)
            {
                Armed = false;
                throw new DbUpdateException("Injected failure during second issue.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
