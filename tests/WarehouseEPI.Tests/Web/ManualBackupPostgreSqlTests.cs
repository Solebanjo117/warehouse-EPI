using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Web.Backups;
using WarehouseEPI.Web.Branding;
using WarehouseEPI.Web.Locations;

namespace WarehouseEPI.Tests.Web;

public sealed class ManualBackupPostgreSqlTests
{
    [WindowsBackupFact]
    public async Task Read_only_application_role_can_create_a_portable_backup_and_recover_its_paired_pin_key()
    {
        using var clusterFixture = new ManualBackupTests.BackupFixture();
        var pgBin = @"C:\Program Files\PostgreSQL\18\bin";
        var cluster = Path.Combine(clusterFixture.Directory, "cluster");
        var initialized = await RunAsync(Path.Combine(pgBin, "initdb.exe"),
            ["-D", cluster, "-U", "backup_test_admin", "--auth=trust", "--encoding=UTF8", "--locale=C"]);
        Assert.True(initialized.ExitCode == 0, initialized.Output);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var started = await RunAsync(Path.Combine(pgBin, "pg_ctl.exe"),
            ["-D", cluster, "-l", Path.Combine(clusterFixture.Directory, "postgres.log"), "-o", $"-h 127.0.0.1 -p {port}", "-w", "start"]);
        Assert.True(started.ExitCode == 0, started.Output);
        try { await VerifyAsync($"Host=127.0.0.1;Port={port};Username=backup_test_admin;Database=postgres"); }
        finally
        {
            var stopped = await RunAsync(Path.Combine(pgBin, "pg_ctl.exe"), ["-D", cluster, "-w", "-m", "immediate", "stop"]);
            Assert.True(stopped.ExitCode == 0, stopped.Output);
        }
    }

    private static async Task VerifyAsync(string source)
    {
        var name = "warehouse_epi_manual_backup_test_" + Guid.NewGuid().ToString("N");
        var role = "epi_backup_test_" + Guid.NewGuid().ToString("N");
        var rolePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var adminBuilder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        var roleCreated = false;
        try
        {
            var testBuilder = new NpgsqlConnectionStringBuilder(source) { Database = name, Pooling = false };
            await using (var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testBuilder.ConnectionString).Options))
            {
                await db.Database.MigrateAsync();
                Assert.Matches("^epi_backup_test_[a-f0-9]{32}$", role);
                Assert.Matches("^[A-F0-9]{64}$", rolePassword);
                await db.Database.OpenConnectionAsync();
                await using (var command = new NpgsqlCommand($"CREATE ROLE \"{role}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{rolePassword}';", (NpgsqlConnection)db.Database.GetDbConnection()))
                    await command.ExecuteNonQueryAsync();
                roleCreated = true;
                await using (var command = new NpgsqlCommand($"GRANT CONNECT ON DATABASE \"{name}\" TO \"{role}\"; GRANT USAGE ON SCHEMA public TO \"{role}\"; GRANT SELECT ON ALL TABLES IN SCHEMA public TO \"{role}\"; GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO \"{role}\";", (NpgsqlConnection)db.Database.GetDbConnection()))
                    await command.ExecuteNonQueryAsync();
            }
            using var fixture = new ManualBackupTests.BackupFixture();
            var values = new Dictionary<string, string?>(fixture.Configuration)
            {
                ["Backups:PgDumpPath"] = @"C:\Program Files\PostgreSQL\18\bin\pg_dump.exe",
                ["Backups:PgRestorePath"] = @"C:\Program Files\PostgreSQL\18\bin\pg_restore.exe",
                ["Branding:StorageDirectory"] = Path.Combine(fixture.Directory, "branding"),
                ["WarehouseMap:ReferenceStorageDirectory"] = Path.Combine(fixture.Directory, "references")
            };
            var environment = new ManualBackupTests.TestEnvironment();
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var settings = new ManualBackupSettings(environment, config);
            Assert.Null(settings.GetUnavailableReason());
            var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jTXYAAAAASUVORK5CYII=");
            var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            var logoName = Guid.NewGuid().ToString("N") + ".png";
            var referenceName = hash + ".png";
            Directory.CreateDirectory(values["Branding:StorageDirectory"]!);
            Directory.CreateDirectory(values["WarehouseMap:ReferenceStorageDirectory"]!);
            await File.WriteAllBytesAsync(Path.Combine(values["Branding:StorageDirectory"]!, logoName), image);
            var referencePath = Path.Combine(values["WarehouseMap:ReferenceStorageDirectory"]!, referenceName);
            await File.WriteAllBytesAsync(referencePath, image);
            await using (var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testBuilder.ConnectionString).Options))
            {
                var actor = new User { FullName = "Backup asset fixture", RoleId = (await db.Roles.SingleAsync(item => item.Code == "ADMIN")).Id,
                    PinLookup = Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(ManualBackupTests.PinKey), "1470"u8.ToArray())).ToLowerInvariant(),
                    PinHash = new PinProtector(ManualBackupTests.PinKey).Hash("1470") };
                db.Users.Add(actor);
                var business = await db.BusinessSettings.SingleOrDefaultAsync();
                if (business is null)
                {
                    business = new BusinessSettings { BusinessName = "Fixture", WarehouseName = "Fixture", WarehouseCode = "FX", TimeZoneId = "UTC", UpdatedByUserId = actor.Id };
                    db.BusinessSettings.Add(business);
                }
                business.LogoFileName = logoName; business.LogoHash = hash; business.LogoContentType = "image/png";
                if (!await db.WarehouseMapLayouts.AnyAsync()) db.WarehouseMapLayouts.Add(new WarehouseMapLayout());
                db.WarehouseMapReferenceImages.Add(new WarehouseMapReferenceImage { OriginalFileName = "fixture.png", StoredFileName = referenceName,
                    ContentType = "image/png", Sha256 = hash, PixelWidth = 1, PixelHeight = 1, Width = 1, Height = 1, CreatedByUserId = actor.Id });
                await db.SaveChangesAsync();
            }
            var readonlyBuilder = new NpgsqlConnectionStringBuilder(testBuilder.ConnectionString)
            { Username = role, Password = rolePassword };
            var services = new ServiceCollection();
            services.AddDbContext<WarehouseDbContext>(options => options.UseNpgsql(readonlyBuilder.ConnectionString));
            await using var provider = services.BuildServiceProvider();
            var builder = new PostgresManualBackupBuilder(settings, provider.GetRequiredService<IServiceScopeFactory>(),
                new BrandingStorage(environment, config), new WarehouseMapReferenceStorage(environment, config, TimeProvider.System));
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = ManualBackupEncryption.DeriveKey(ManualBackupTests.Password, salt);
            var work = Path.Combine(fixture.Directory, "work"); Directory.CreateDirectory(work);
            var encrypted = Path.Combine(fixture.Directory, "backup.webackup");
            try { await builder.BuildAsync(work, encrypted, DateTimeOffset.UtcNow, key, salt, CancellationToken.None); }
            finally { CryptographicOperations.ZeroMemory(key); }
            using (var zip = new ZipArchive(new MemoryStream(ManualBackupTests.Decrypt(await File.ReadAllBytesAsync(encrypted), ManualBackupTests.Password))))
            {
                Assert.Equal(2, zip.Entries.Count);
                await using var secret = zip.GetEntry("secrets.json")!.Open();
                var data = await JsonDocument.ParseAsync(secret);
                Assert.Equal(ManualBackupTests.PinKey, data.RootElement.GetProperty("PinLookupKey").GetString());
            }
            var root = FindRoot();
            var wrapper = Path.Combine(fixture.Directory, "recover.ps1");
            // Public test fixture password only; production recovery always reads a SecureString interactively.
            await File.WriteAllTextAsync(wrapper, "param($Tool,$Package,$Destination)\n" +
                "function Read-Host { param($Prompt,[switch]$AsSecureString) ConvertTo-SecureString 'Backup fixture phrase 2026' -AsPlainText -Force }\n" +
                "& $Tool -PackagePath $Package -DestinationDirectory $Destination\n");
            var destination = Path.Combine(fixture.Directory, "recovered");
            var opened = await RunAsync("pwsh", ["-NoProfile", "-File", wrapper, "-Tool",
                Path.Combine(root, "scripts", "security", "Open-WarehouseEpiManualBackup.ps1"), "-Package", encrypted, "-Destination", destination]);
            Assert.True(opened.ExitCode == 0, opened.Output);
            using var paired = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "pinlookupkey.json")));
            Assert.Equal(ManualBackupTests.PinKey, paired.RootElement.GetProperty("PinLookupKey").GetString());
            Assert.False(File.Exists(Path.Combine(destination, ".recovery.zip")));
            var migration = Path.Combine(destination, "WarehouseEPI-migration.zip");
            using var package = ZipFile.OpenRead(migration);
            Assert.NotNull(package.GetEntry("branding/" + logoName));
            var referenceEntry = Assert.Single(package.Entries, entry => entry.FullName.StartsWith("references/", StringComparison.Ordinal));
            using (var referencePackage = new ZipArchive(referenceEntry.Open())) Assert.NotNull(referencePackage.GetEntry(referenceName));
            var dumpEntry = Assert.Single(package.Entries, entry => entry.FullName.StartsWith("database/", StringComparison.Ordinal));
            var dump = Path.Combine(fixture.Directory, "restored.dump"); dumpEntry.ExtractToFile(dump);
            var restoredName = "warehouse_epi_manual_restore_test_" + Guid.NewGuid().ToString("N");
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{restoredName}\"", admin)) await create.ExecuteNonQueryAsync();
            try
            {
                var restored = await RunAsync(settings.PgRestorePath,
                    ["--no-owner", "--no-privileges", "--exit-on-error", "--no-password", "--dbname=" + restoredName, dump], adminBuilder);
                Assert.Equal(0, restored.ExitCode);
                var restoredBuilder = new NpgsqlConnectionStringBuilder(source) { Database = restoredName, Pooling = false };
                await using var restoredDb = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(restoredBuilder.ConnectionString).Options);
                Assert.Contains(await restoredDb.Roles.Select(item => item.Code).ToListAsync(), code => code == "ADMIN");
                Assert.NotEmpty(await restoredDb.Database.GetAppliedMigrationsAsync());
                Assert.Equal(logoName, (await restoredDb.BusinessSettings.SingleAsync()).LogoFileName);
                Assert.Equal(referenceName, (await restoredDb.WarehouseMapReferenceImages.SingleAsync()).StoredFileName);
                var expectedLookup = Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(paired.RootElement.GetProperty("PinLookupKey").GetString()!), "1470"u8.ToArray())).ToLowerInvariant();
                Assert.Equal(expectedLookup, (await restoredDb.Users.SingleAsync()).PinLookup);
            }
            finally
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{restoredName}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
            }
            var migrationsPath = Path.Combine(fixture.Directory, "migration-ids.json");
            await using (var sourceDb = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(testBuilder.ConnectionString).Options))
                await File.WriteAllTextAsync(migrationsPath, JsonSerializer.Serialize(await sourceDb.Database.GetAppliedMigrationsAsync()));
            var restoreFlow = await RunAsync("pwsh", ["-NoProfile", "-File", Path.Combine(root, "tests", "powershell", "BackupRestore.PostgreSql.ps1"),
                "-RepositoryRoot", root, "-FixtureRoot", Path.Combine(fixture.Directory, "restore-server-fixture"), "-Port", adminBuilder.Port.ToString(),
                "-SourceDatabase", name, "-PackagePath", migration, "-KeyPath", Path.Combine(destination, "pinlookupkey.json"),
                "-LogoPath", Path.Combine(values["Branding:StorageDirectory"]!, logoName), "-ReferencePath", referencePath,
                "-MigrationIdsPath", migrationsPath]);
            Assert.True(restoreFlow.ExitCode == 0, restoreFlow.Output);
            Assert.Contains("interrupted rename: passed", restoreFlow.Output);
            File.Delete(referencePath);
            var missingWork = Path.Combine(fixture.Directory, "missing-work"); Directory.CreateDirectory(missingWork);
            var missingKey = ManualBackupEncryption.DeriveKey(ManualBackupTests.Password, salt);
            try
            {
                await Assert.ThrowsAsync<IOException>(() => builder.BuildAsync(missingWork, Path.Combine(fixture.Directory, "missing.webackup"),
                    DateTimeOffset.UtcNow, missingKey, salt, CancellationToken.None));
                Assert.False(File.Exists(Path.Combine(fixture.Directory, "missing.webackup")));
            }
            finally { CryptographicOperations.ZeroMemory(missingKey); }
        }
        finally
        {
            await using (var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin)) await drop.ExecuteNonQueryAsync();
            if (roleCreated) { await using var drop = new NpgsqlCommand($"DROP ROLE \"{role}\"", admin); await drop.ExecuteNonQueryAsync(); }
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, string[] arguments, NpgsqlConnectionStringBuilder? connection = null)
    {
        // pg_ctl's detached server can retain inherited pipe handles; wait for pg_ctl, not pipe EOF.
        var capture = !Path.GetFileNameWithoutExtension(executable).Equals("pg_ctl", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = capture, RedirectStandardError = capture };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (connection is not null)
        {
            start.Environment["PGHOST"] = connection.Host; start.Environment["PGPORT"] = connection.Port.ToString();
            start.Environment["PGUSER"] = connection.Username; start.Environment["PGPASSWORD"] = connection.Password;
            if (!string.IsNullOrWhiteSpace(connection.Passfile)) start.Environment["PGPASSFILE"] = connection.Passfile;
        }
        using var process = Process.Start(start)!;
        var output = capture ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var error = capture ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    private sealed class WindowsBackupFactAttribute : FactAttribute
    {
        public WindowsBackupFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "The production manual backup and ACL recovery use Windows PostgreSQL tools.";
        }
    }
}
