using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Tests.Inventory;

public sealed class MaterialIncidentPostgreSqlTests
{
    // Uses fresh, uniquely named test databases; never drops or migrates a configured application database.
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MaterialIncident_migration_concurrent_retries_atomic_failure_and_backup_restore()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure PostgreSQL test credentials.");
        var builder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var name = "warehouse_epi_incidents_test_" + Guid.NewGuid().ToString("N");
        var restored = name + "_restore";
        var created = new List<string>();
        var dump = Path.Combine(Path.GetTempPath(), name + ".dump");
        await using var admin = new NpgsqlConnection(builder.ConnectionString); await admin.OpenAsync();
        try
        {
            foreach (var database in new[] { name, restored })
            { await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin); await create.ExecuteNonQueryAsync(); created.Add(database); }
            builder.Database = name; var connection = builder.ConnectionString;
            WarehouseDbContext Open(string text) => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(text).Options);
            UserPinService Pins(WarehouseDbContext db) => new(db, new PinProtector(PostgreSqlInventoryFixture.LookupKey));
            await using var db = Open(connection); await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var user = new User { FullName = "Incident integration", RoleId = 1, PinHash = "", PinLookup = "" };
            await Pins(db).AssignAsync(user, "2468");
            var product = new Product { Sku = "INCIDENT-PG", BaseUnitId = 1 }; var location = new Location { Code = "STAGING", Kind = LocationKind.Area };
            db.AddRange(user, product, location); await db.SaveChangesAsync();
            var movement = new InventoryMovementService(db, Pins(db), TimeProvider.System);
            Assert.Equal(InventoryMovementStatus.Success, (await movement.ConfirmAsync(new(Guid.NewGuid(), InventoryMovementType.Entry, "2468", [new(product.Id, 20, DestinationLocationId: location.Id)]))).Status);
            var plate = await db.PalletPlates.SingleAsync();
            var service = new MaterialIncidentService(db, Pins(db), TimeProvider.System);
            var context = await service.ContextAsync(new(PlateId: plate.Id)); Assert.NotNull(context);
            var photo = new IncidentPhotoInput("evidence.png", "image/png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aTioAAAAASUVORK5CYII="));
            var report = new IncidentReport(Guid.NewGuid(), new(PlateId: plate.Id), context.Token, MaterialIncidentScope.Receiving, MaterialIncidentKind.Damage,
                MaterialIncidentDifference.Undetermined, 30, "Daño reportado", "2468", [photo]);
            async Task<IncidentResult> Report(IncidentReport input)
            { await using var other = Open(connection); return await new MaterialIncidentService(other, Pins(other), TimeProvider.System).ReportAsync(input); }
            var results = await Task.WhenAll(Report(report), Report(report));
            Assert.All(results, r => Assert.Null(r.Error)); Assert.Equal(results[0].Id, results[1].Id);
            Assert.Single(await db.MaterialIncidents.AsNoTracking().ToListAsync()); Assert.Single(await db.MaterialIncidentPhotos.ToListAsync());
            var follow = new IncidentFollowUp(Guid.NewGuid(), results[0].Id!.Value, 1, "Comment", "Seguimiento concurrente", null, "2468", []);
            async Task<IncidentResult> Follow(IncidentFollowUp input)
            { await using var other = Open(connection); return await new MaterialIncidentService(other, Pins(other), TimeProvider.System).FollowUpAsync(input); }
            var race = await Task.WhenAll(Follow(follow), Follow(follow with { OperationId = Guid.NewGuid() }));
            Assert.Single(race, r => r.Error is null); Assert.Single(race, r => r.Error is not null);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE material_incident_photos ADD CONSTRAINT reject_test_photo CHECK (\"Name\" <> 'reject.png')");
            Assert.NotNull((await Report(report with { OperationId = Guid.NewGuid(), Photos = [photo with { Name = "reject.png" }] })).Error);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE material_incident_photos DROP CONSTRAINT reject_test_photo");
            Assert.Single(await db.MaterialIncidents.AsNoTracking().ToListAsync()); Assert.Single(await db.MaterialIncidentPhotos.ToListAsync());
            Assert.Equal(20, await db.InventoryBalances.SumAsync(b => b.Quantity));
            var query = new MaterialIncidentQuery(db, service);
            Assert.Single((await query.ListAsync(new(ProductCode: "PG", LocationCode: "STAGING"))).Items);
            Assert.Single((await query.ListAsync(new(Search: $"INC-{results[0].Id:N}"[..12]))).Items);
            Assert.Equal(1, (await query.CountsAsync([new(location.Id, product.Id, location.Id)]))[location.Id].Reported);
            await PgTool("pg_dump", builder, ["--format=custom", "--no-owner", "--file", dump, name]);
            await PgTool("pg_restore", builder, ["--no-owner", "--no-privileges", "--dbname", restored, dump]);
            builder.Database = restored; await using var restoredDb = Open(builder.ConnectionString);
            Assert.Equal(results[0].Id, (await restoredDb.MaterialIncidents.SingleAsync()).Id);
            Assert.Equal(2, await restoredDb.MaterialIncidentEvents.CountAsync());
            Assert.Equal(photo.Content, (await restoredDb.MaterialIncidentPhotos.SingleAsync()).Content);
        }
        finally
        {
            foreach (var database in created.AsEnumerable().Reverse())
            {
                if (!database.StartsWith("warehouse_epi_incidents_test_", StringComparison.Ordinal)) continue;
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
            }
            if (File.Exists(dump)) File.Delete(dump);
        }
    }

    private static async Task PgTool(string tool, NpgsqlConnectionStringBuilder connection, string[] arguments)
    {
        var executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "18", "bin", tool + ".exe") : tool;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "--host", connection.Host!, "--port", connection.Port.ToString(), "--username", connection.Username!, "--no-password" }.Concat(arguments)) start.ArgumentList.Add(argument);
        start.Environment["PGPASSWORD"] = connection.Password;
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(); await stdout;
        Assert.True(process.ExitCode == 0, await stderr);
    }
}
