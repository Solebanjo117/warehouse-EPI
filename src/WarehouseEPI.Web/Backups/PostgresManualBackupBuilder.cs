using System.Data;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Branding;
using WarehouseEPI.Web.Locations;

namespace WarehouseEPI.Web.Backups;

public sealed class PostgresManualBackupBuilder(ManualBackupSettings settings, IServiceScopeFactory scopeFactory,
    BrandingStorage branding, WarehouseMapReferenceStorage references) : IManualBackupBuilder
{
    public async Task BuildAsync(string workingDirectory, string encryptedPath, DateTimeOffset startedAt,
        byte[] key, byte[] salt, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var connection = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        var baseName = $"warehouseEPI-{startedAt:yyyyMMdd-HHmmss}";
        var dump = Path.Combine(workingDirectory, baseName + ".dump");
        var referenceZip = Path.Combine(workingDirectory, baseName + "-references.zip");
        var package = Path.Combine(workingDirectory, "migration.zip");
        var assets = new List<BackupFile>();

        // Both the file references and pg_dump use the same database snapshot. Ordinary DML continues.
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken))
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "SELECT pg_export_snapshot()";
            var snapshot = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
            var logo = await db.BusinessSettings.AsNoTracking()
                .Select(item => new { item.LogoFileName, item.LogoHash }).SingleOrDefaultAsync(cancellationToken);
            var images = await db.WarehouseMapReferenceImages.AsNoTracking()
                .Select(item => new { item.StoredFileName, item.Sha256 }).Distinct().ToListAsync(cancellationToken);
            foreach (var image in images)
                assets.Add(await CopyAssetAsync(references.GetPath(image.StoredFileName), image.StoredFileName,
                    image.Sha256, "reference", workingDirectory, cancellationToken));
            if (logo?.LogoFileName is not null)
                assets.Add(await CopyAssetAsync(branding.GetPath(logo.LogoFileName), logo.LogoFileName,
                    logo.LogoHash, "branding", workingDirectory, cancellationToken));
            await RunAsync(settings.PgDumpPath,
                ["--format=custom", "--no-owner", "--no-privileges", "--no-password", "--lock-wait-timeout=10s",
                    "--snapshot=" + snapshot, "--file=" + dump], connection, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        await RunAsync(settings.PgRestorePath, ["--list", dump], null, cancellationToken);

        using (var archive = ZipFile.Open(referenceZip, ZipArchiveMode.Create))
        {
            foreach (var asset in assets.Where(file => file.Kind == "reference"))
                archive.CreateEntryFromFile(asset.LocalPath, Path.GetFileName(asset.LocalPath), CompressionLevel.Optimal);
            await WriteJsonAsync(archive, "manifest.json", new
            {
                SchemaVersion = 1,
                DatabaseBackup = baseName + ".dump",
                CreatedAtUtc = startedAt,
                Files = assets.Where(file => file.Kind == "reference").Select(file => new
                { Name = Path.GetFileName(file.LocalPath), file.Length, file.Sha256 })
            }, cancellationToken);
        }
        var files = new List<BackupFile>
        {
            await DescribeAsync(dump, "database/" + Path.GetFileName(dump), "database", cancellationToken),
            await DescribeAsync(referenceZip, "references/" + Path.GetFileName(referenceZip), "references", cancellationToken)
        };
        files.AddRange(assets.Where(file => file.Kind == "branding"));
        var instructions = Path.Combine(workingDirectory, "RESTORE.txt");
        await File.WriteAllTextAsync(instructions,
            "Warehouse EPI: respaldo manual.\n" +
            "La estructura PostgreSQL fue comprobada con pg_restore --list; IT debe probar una restauración aislada.\n" +
            "Use Test-WarehouseEpiMigrationBackup.ps1 y Restore-WarehouseEpiMigrationBackup.ps1 en un destino vacío.\n" +
            "La PinLookupKey pareada se recupera del archivo cifrado mediante Open-WarehouseEpiManualBackup.ps1.\n" +
            "No genere otra clave para esta base. Configure credenciales PostgreSQL y HTTPS en el destino.\n" +
            "El respaldo es una instantánea: no incluye operaciones posteriores a su inicio.\n", Encoding.UTF8, cancellationToken);
        files.Add(await DescribeAsync(instructions, "RESTORE.txt", "instructions", cancellationToken));
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            foreach (var file in files)
                archive.CreateEntryFromFile(file.LocalPath, file.Path, CompressionLevel.Optimal);
            await WriteJsonAsync(archive, "manifest.json", new
            {
                SchemaVersion = 1,
                PackageType = "WarehouseEPI-MigrationBackup",
                CreatedAtUtc = startedAt,
                DatabaseName = "warehouseEPI",
                SourceDatabaseName = connection.Database,
                ApplicationVersion = typeof(Program).Assembly.GetName().Version?.ToString(),
                Verification = "pg_restore --list (restore rehearsal required)",
                ContainsSecrets = false,
                RequiredExternalSecrets = new[] { "Security:PinLookupKey", "PostgreSQL credentials", "LAN CA PFX" },
                Files = files.Select(file => new { file.Path, file.Kind, file.Length, file.Sha256 })
            }, cancellationToken);
        }
        var packageInfo = await DescribeAsync(package, "migration.zip", "migration", cancellationToken);
        await ManualBackupEncryption.WriteAsync(encryptedPath, key, salt, async (encrypted, token) =>
        {
            using var archive = new ZipArchive(encrypted, ZipArchiveMode.Create, leaveOpen: true);
            await using (var entry = archive.CreateEntry("migration.zip", CompressionLevel.NoCompression).Open())
            await using (var source = File.OpenRead(package))
                await source.CopyToAsync(entry, token);
            // This entry goes directly into the encryption stream; the key is never written to a plaintext file.
            await WriteJsonAsync(archive, "secrets.json", new
            {
                SchemaVersion = 1,
                MigrationPackageSha256 = packageInfo.Sha256,
                PinLookupKey = settings.PinLookupKey
            }, token);
        }, cancellationToken);
    }

    private static async Task<BackupFile> CopyAssetAsync(string? source, string fileName, string? expectedHash,
        string kind, string workingDirectory, CancellationToken token)
    {
        if (source is null || expectedHash is null ||
            new FileInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Missing backup asset.");
        var directory = Path.Combine(workingDirectory, kind);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, fileName);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await input.CopyToAsync(output, token);
        var file = await DescribeAsync(destination, kind + "/" + fileName, kind, token);
        if (!string.Equals(file.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Changed backup asset.");
        return file;
    }

    private static async Task<BackupFile> DescribeAsync(string localPath, string archivePath, string kind, CancellationToken token)
    {
        await using var file = File.OpenRead(localPath);
        return new(localPath, archivePath, kind, file.Length,
            Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant());
    }

    private static async Task WriteJsonAsync<T>(ZipArchive archive, string name, T content, CancellationToken token)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await JsonSerializer.SerializeAsync(stream, content, cancellationToken: token);
    }

    private static async Task RunAsync(string executable, string[] arguments, NpgsqlConnectionStringBuilder? connection,
        CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Do not inherit another server's libpq configuration or expose credentials in command-line arguments.
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("PG", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        if (connection is not null)
        {
            start.Environment["PGHOST"] = connection.Host;
            start.Environment["PGPORT"] = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["PGDATABASE"] = connection.Database;
            start.Environment["PGUSER"] = connection.Username;
            start.Environment["PGPASSWORD"] = connection.Password;
            start.Environment["PGSSLMODE"] = connection.SslMode.ToString().ToLowerInvariant().Replace("verifyca", "verify-ca", StringComparison.Ordinal)
                .Replace("verifyfull", "verify-full", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(connection.RootCertificate)) start.Environment["PGSSLROOTCERT"] = connection.RootCertificate;
        }
        using var process = Process.Start(start) ?? throw new IOException("Backup tool unavailable.");
        // Drain without retaining tool output: PostgreSQL messages may contain sensitive details.
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw new IOException("Backup tool failed.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            start.Environment.Remove("PGPASSWORD");
        }
    }

    private sealed record BackupFile(string LocalPath, string Path, string Kind, long Length, string Sha256);
}
