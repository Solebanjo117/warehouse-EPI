using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WarehouseEPI.Web.Backups;

public sealed record RestorePackageSummary(DateTimeOffset CreatedAtUtc, string? ApplicationVersion,
    long DatabaseBytes, int Logos, int References, string PackageSha256);
public sealed class RestorePackageException(string message) : Exception(message);

public sealed class RestorePackageInspector
{
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;

    public static byte[] ReadSalt(string path)
    {
        using var input = File.OpenRead(path);
        var header = new byte[44]; input.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual("WEPBK001"u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40)) != ManualBackupEncryption.Iterations ||
            input.Length < 92 || input.Length > MaxUploadBytes || (input.Length - 76) % 16 != 0)
            throw new RestorePackageException("El archivo no es un respaldo protegido compatible.");
        return header[8..24];
    }

    public async Task<RestorePackageSummary> InspectAsync(string directory, byte[] key, CancellationToken token)
    {
        var decrypted = Path.Combine(directory, ".decrypted.zip");
        var package = Path.Combine(directory, "migration.zip");
        try
        {
            await AuthenticateAndDecryptAsync(Path.Combine(directory, "upload.webackup"), decrypted, key, token);
            using (var outer = ZipFile.OpenRead(decrypted))
            {
                if (outer.Entries.Count != 2 || outer.Entries.Count(entry => entry.FullName == "migration.zip") != 1 ||
                    outer.Entries.Count(entry => entry.FullName == "secrets.json") != 1)
                    throw new RestorePackageException("El contenido del respaldo no es compatible.");
                var secretsEntry = outer.GetEntry("secrets.json")!;
                if (secretsEntry.Length > 4096) throw new RestorePackageException("El contenido del respaldo no es compatible.");
                using var secrets = await ReadJsonAsync(secretsEntry, 4096, token);
                var root = secrets.RootElement;
                if (root.GetProperty("SchemaVersion").GetInt32() != 1 ||
                    Convert.FromBase64String(root.GetProperty("PinLookupKey").GetString()!).Length < 32)
                    throw new RestorePackageException("El respaldo no contiene una clave de NIP válida.");
                await ExtractLimitedAsync(outer.GetEntry("migration.zip")!, package, MaxUploadBytes, token);
                var hash = await HashAsync(package, token);
                if (!string.Equals(hash, root.GetProperty("MigrationPackageSha256").GetString(), StringComparison.Ordinal))
                    throw new RestorePackageException("La clave de NIP no corresponde al respaldo.");
                // Protected staging is outside wwwroot. Only the local maintenance worker consumes this key.
                await File.WriteAllTextAsync(Path.Combine(directory, "pinlookupkey.json"), root.GetRawText(), token);
            }
            return await ValidateMigrationAsync(package, token);
        }
        finally { if (File.Exists(decrypted)) File.Delete(decrypted); }
    }

    private static async Task AuthenticateAndDecryptAsync(string source, string destination, byte[] key, CancellationToken token)
    {
        await using var input = File.OpenRead(source);
        var header = new byte[44]; await input.ReadExactlyAsync(header, token);
        _ = ReadSalt(source);
        var remaining = input.Length - 76;
        var buffer = new byte[81920];
        using (var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.AsSpan(32)))
        {
            mac.AppendData(header);
            while (remaining > 0)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), token);
                if (count == 0) throw new EndOfStreamException();
                mac.AppendData(buffer, 0, count); remaining -= count;
            }
            var tag = new byte[32]; await input.ReadExactlyAsync(tag, token);
            if (!CryptographicOperations.FixedTimeEquals(tag, mac.GetHashAndReset()))
                throw new RestorePackageException("La contraseña es incorrecta o el respaldo fue alterado.");
        }
        input.Position = 44;
        using var aes = Aes.Create(); aes.Key = key[..32]; aes.IV = header[24..40];
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await using var decrypt = new CryptoStream(output, aes.CreateDecryptor(), CryptoStreamMode.Write);
        remaining = input.Length - 76;
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), token);
            if (count == 0) throw new EndOfStreamException();
            await decrypt.WriteAsync(buffer.AsMemory(0, count), token); remaining -= count;
        }
        await decrypt.FlushFinalBlockAsync(token);
    }

    private static async Task<RestorePackageSummary> ValidateMigrationAsync(string package, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(package);
        if (archive.Entries.Count > 5000 || archive.Entries.Any(entry => entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') ||
            entry.FullName.Split('/').Contains("..", StringComparer.Ordinal)) || archive.Entries.Sum(entry => entry.Length) > MaxExpandedBytes ||
            archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
            throw new RestorePackageException("El respaldo contiene rutas o tamaños no permitidos.");
        var manifest = archive.GetEntry("manifest.json") ?? throw new RestorePackageException("Falta el manifiesto del respaldo.");
        if (manifest.Length > 1024 * 1024) throw new RestorePackageException("El manifiesto del respaldo es demasiado grande.");
        using var document = await ReadJsonAsync(manifest, 1024 * 1024, token);
        var root = document.RootElement;
        if (root.GetProperty("SchemaVersion").GetInt32() != 1 || root.GetProperty("PackageType").GetString() != "WarehouseEPI-MigrationBackup" ||
            root.GetProperty("ContainsSecrets").GetBoolean() || root.GetProperty("DatabaseName").GetString() != "warehouseEPI")
            throw new RestorePackageException("El manifiesto del respaldo no es compatible.");
        var requiredSecrets = root.GetProperty("RequiredExternalSecrets").EnumerateArray().Select(value => value.GetString()).ToArray();
        if (new[] { "Security:PinLookupKey", "PostgreSQL credentials", "LAN CA PFX" }.Any(value => !requiredSecrets.Contains(value, StringComparer.Ordinal)))
            throw new RestorePackageException("El manifiesto del respaldo no es compatible.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        var dumps = root.GetProperty("Files").EnumerateArray().Where(file => file.GetProperty("Kind").GetString() == "database").Take(2).ToArray();
        if (dumps.Length != 1) throw new RestorePackageException("El respaldo está incompleto.");
        var dumpName = Path.GetFileName(dumps[0].GetProperty("Path").GetString());
        var databases = 0; var references = 0; var instructions = 0; var logos = 0; var referenceImages = 0; long databaseBytes = 0;
        foreach (var file in root.GetProperty("Files").EnumerateArray())
        {
            var name = file.GetProperty("Path").GetString()!;
            var kind = file.GetProperty("Kind").GetString();
            var valid = kind switch
            {
                "database" => Regex.IsMatch(name, "^database/warehouseEPI-[0-9]{8}-[0-9]{6}\\.dump$", RegexOptions.CultureInvariant),
                "references" => Regex.IsMatch(name, "^references/warehouseEPI-[0-9]{8}-[0-9]{6}-references\\.zip$", RegexOptions.CultureInvariant),
                "branding" => Regex.IsMatch(name, "^branding/[a-f0-9]{32}\\.(png|jpg|webp)$", RegexOptions.CultureInvariant),
                "instructions" => name == "RESTORE.txt",
                _ => false
            };
            var entry = archive.GetEntry(name);
            if (!valid || !names.Add(name) || entry is null || entry.Length != file.GetProperty("Length").GetInt64())
                throw new RestorePackageException("El respaldo contiene componentes no permitidos.");
            var hash = await HashEntryAsync(entry, token);
            if (hash != file.GetProperty("Sha256").GetString()) throw new RestorePackageException("La integridad del respaldo no coincide con su manifiesto.");
            switch (kind)
            {
                case "database": databases++; databaseBytes = entry.Length; break;
                case "references": references++; referenceImages = await ValidateReferencesAsync(entry, Path.GetDirectoryName(package)!, dumpName!, token); break;
                case "instructions": instructions++; break;
                case "branding": logos++; break;
            }
        }
        if (databases != 1 || references != 1 || instructions != 1 || names.Count != archive.Entries.Count)
            throw new RestorePackageException("El respaldo está incompleto.");
        return new(root.GetProperty("CreatedAtUtc").GetDateTimeOffset().ToUniversalTime(),
            root.TryGetProperty("ApplicationVersion", out var version) ? version.GetString() : null,
            databaseBytes, logos, referenceImages, await HashAsync(package, token));
    }

    private static async Task<int> ValidateReferencesAsync(ZipArchiveEntry entry, string directory, string dumpName, CancellationToken token)
    {
        // ZipArchive buffers a nonseekable nested archive in RAM; use bounded private disk staging instead.
        var temporary = Path.Combine(directory, ".references.zip");
        await ExtractLimitedAsync(entry, temporary, 256L * 1024 * 1024, token);
        try { return await ValidateReferencesFileAsync(temporary, dumpName, token); }
        finally { File.Delete(temporary); }
    }

    private static async Task<int> ValidateReferencesFileAsync(string path, string dumpName, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 5000 || archive.Entries.Any(item => item.FullName != "manifest.json" &&
            !Regex.IsMatch(item.FullName, "^[a-f0-9]{64}\\.(png|jpg|webp)$", RegexOptions.CultureInvariant)) ||
            archive.Entries.Sum(item => item.Length) > MaxExpandedBytes || archive.Entries.Select(item => item.FullName).Distinct().Count() != archive.Entries.Count)
            throw new RestorePackageException("Los fondos del croquis no son válidos.");
        var manifest = archive.GetEntry("manifest.json") ?? throw new RestorePackageException("Falta el manifiesto del croquis.");
        if (manifest.Length > 1024 * 1024) throw new RestorePackageException("El manifiesto del croquis es demasiado grande.");
        using var document = await ReadJsonAsync(manifest, 1024 * 1024, token);
        if (document.RootElement.GetProperty("SchemaVersion").GetInt32() != 1 ||
            document.RootElement.GetProperty("DatabaseBackup").GetString() != dumpName) throw new RestorePackageException("Los fondos del croquis no son válidos.");
        var names = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        foreach (var file in document.RootElement.GetProperty("Files").EnumerateArray())
        {
            var name = file.GetProperty("Name").GetString()!;
            var content = archive.GetEntry(name);
            if (!names.Add(name) || content is null || content.Length != file.GetProperty("Length").GetInt64())
                throw new RestorePackageException("Los fondos del croquis están incompletos.");
            if (await HashEntryAsync(content, token) != file.GetProperty("Sha256").GetString())
                throw new RestorePackageException("La integridad de los fondos del croquis no coincide.");
        }
        if (names.Count != archive.Entries.Count) throw new RestorePackageException("Los fondos del croquis no son válidos.");
        return names.Count - 1;
    }

    private static async Task ExtractLimitedAsync(ZipArchiveEntry entry, string path, long maximum, CancellationToken token)
    {
        if (entry.Length is < 1 || entry.Length > maximum) throw new RestorePackageException("El respaldo supera el límite de tamaño.");
        await using var source = entry.Open(); await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await CopyBoundedAsync(source, destination, entry.Length, token);
        if (destination.Length != entry.Length) throw new RestorePackageException("El respaldo está incompleto.");
    }

    private static async Task<JsonDocument> ReadJsonAsync(ZipArchiveEntry entry, long maximum, CancellationToken token)
    {
        if (entry.Length < 1 || entry.Length > maximum) throw new RestorePackageException("El manifiesto del respaldo es demasiado grande.");
        await using var input = entry.Open(); using var output = new MemoryStream();
        await CopyBoundedAsync(input, output, entry.Length, token);
        return JsonDocument.Parse(output.ToArray());
    }

    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = entry.Open(); var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > entry.Length) throw new RestorePackageException("El respaldo supera el límite de tamaño.");
            hash.AppendData(buffer, 0, count);
        }
        if (total != entry.Length) throw new RestorePackageException("El respaldo está incompleto.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken token)
    {
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > maximum) throw new RestorePackageException("El respaldo supera el límite de tamaño.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (total != maximum) throw new RestorePackageException("El respaldo está incompleto.");
    }

    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
}
