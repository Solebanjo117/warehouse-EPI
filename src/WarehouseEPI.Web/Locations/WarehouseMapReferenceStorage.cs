using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace WarehouseEPI.Web.Locations;

public sealed record WarehouseMapStagedReference(
    Guid Token,
    Guid ReferenceId,
    Guid UserId,
    string OriginalFileName,
    string StoredFileName,
    string ContentType,
    string Sha256,
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset ExpiresAt);

public sealed class WarehouseMapReferenceValidationException(string message) : Exception(message);

public sealed class WarehouseMapReferenceStorage
{
    public const long MaxBytes = 2L * 1024 * 1024;
    public const int MaxPixels = 4096;
    private static readonly string ProductionRoot = Path.GetFullPath(@"C:\ProgramData\WarehouseEPI") + Path.DirectorySeparatorChar;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider timeProvider;

    public WarehouseMapReferenceStorage(IWebHostEnvironment environment, IConfiguration configuration, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        DirectoryPath = ResolveDirectory(environment, configuration);
        StagingDirectory = Path.Combine(DirectoryPath, ".staging");
    }

    public string DirectoryPath { get; }
    public string StagingDirectory { get; }

    public async Task<WarehouseMapStagedReference> StageAsync(IFormFile file, Guid userId, CancellationToken token)
    {
        if (file.Length is <= 0 or > MaxBytes)
            throw new WarehouseMapReferenceValidationException("La referencia debe medir entre 1 byte y 2 MiB.");

        await CleanupExpiredAsync(token);
        byte[] content;
        await using (var source = file.OpenReadStream())
        {
            using var buffer = new MemoryStream(checked((int)file.Length));
            await source.CopyToAsync(buffer, token);
            content = buffer.ToArray();
        }
        if (content.LongLength > MaxBytes)
            throw new WarehouseMapReferenceValidationException("La referencia debe medir como máximo 2 MiB.");

        var format = Detect(content) ?? throw new WarehouseMapReferenceValidationException(
            "Usa una imagen PNG, JPEG o WebP válida.");
        if (format.Width is < 1 or > MaxPixels || format.Height is < 1 or > MaxPixels)
            throw new WarehouseMapReferenceValidationException("La referencia no puede exceder 4096 × 4096 píxeles.");

        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var staged = new WarehouseMapStagedReference(Guid.NewGuid(), Guid.NewGuid(), userId,
            SafeName(file.FileName), $"{hash}.{format.Extension}", format.ContentType, hash, format.Width,
            format.Height, timeProvider.GetUtcNow().AddMinutes(30));
        Directory.CreateDirectory(StagingDirectory);
        var contentPath = StageContentPath(staged.Token);
        var metadataPath = StageMetadataPath(staged.Token);
        var temporaryContent = $"{contentPath}.{Guid.NewGuid():N}.tmp";
        var temporaryMetadata = $"{metadataPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryContent, content, token);
            await File.WriteAllTextAsync(temporaryMetadata, JsonSerializer.Serialize(staged, JsonOptions), token);
            File.Move(temporaryContent, contentPath);
            File.Move(temporaryMetadata, metadataPath);
            return staged;
        }
        catch
        {
            DeleteIfExists(temporaryContent);
            DeleteIfExists(temporaryMetadata);
            DeleteIfExists(contentPath);
            DeleteIfExists(metadataPath);
            throw;
        }
    }

    public async Task<WarehouseMapStagedReference?> GetStageAsync(Guid token, Guid userId, CancellationToken cancellationToken)
    {
        var metadataPath = StageMetadataPath(token);
        var contentPath = StageContentPath(token);
        if (!File.Exists(metadataPath) || !File.Exists(contentPath)) return null;
        WarehouseMapStagedReference? value;
        try
        {
            value = JsonSerializer.Deserialize<WarehouseMapStagedReference>(
                await File.ReadAllTextAsync(metadataPath, cancellationToken), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (value is null || value.Token != token || value.UserId != userId || value.ExpiresAt <= timeProvider.GetUtcNow())
            return null;
        await using var stream = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return string.Equals(hash, value.Sha256, StringComparison.Ordinal) ? value : null;
    }

    public async Task<string?> GetStagePathAsync(Guid token, Guid userId, CancellationToken cancellationToken) =>
        await GetStageAsync(token, userId, cancellationToken) is null ? null : StageContentPath(token);

    public async Task<WarehouseMapStagedReference?> PromoteAsync(Guid token, Guid userId, CancellationToken cancellationToken)
    {
        var staged = await GetStageAsync(token, userId, cancellationToken);
        if (staged is null) return null;
        Directory.CreateDirectory(DirectoryPath);
        var finalPath = GetPath(staged.StoredFileName, requireExists: false);
        if (finalPath is null) return null;
        if (!File.Exists(finalPath))
        {
            var temporary = $"{finalPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(StageContentPath(token), temporary, overwrite: false);
                File.Move(temporary, finalPath);
            }
            finally
            {
                DeleteIfExists(temporary);
            }
        }
        return staged;
    }

    public string? GetPath(string? storedFileName, bool requireExists = true)
    {
        if (string.IsNullOrWhiteSpace(storedFileName)
            || !System.Text.RegularExpressions.Regex.IsMatch(storedFileName,
                "^[a-f0-9]{64}\\.(png|jpg|webp)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return null;
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, storedFileName));
        var root = Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (!requireExists || File.Exists(path)) ? path : null;
    }

    public async Task CleanupExpiredAsync(CancellationToken token = default)
    {
        if (!Directory.Exists(StagingDirectory)) return;
        foreach (var metadataPath in Directory.EnumerateFiles(StagingDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            WarehouseMapStagedReference? value = null;
            try { value = JsonSerializer.Deserialize<WarehouseMapStagedReference>(await File.ReadAllTextAsync(metadataPath, token), JsonOptions); }
            catch (JsonException) { }
            if (value is not null && value.ExpiresAt > timeProvider.GetUtcNow()) continue;
            DeleteIfExists(metadataPath);
            DeleteIfExists(Path.ChangeExtension(metadataPath, ".upload"));
        }
    }

    public Task CleanupUnreferencedAsync(IReadOnlyCollection<string> referencedFileNames,
        TimeSpan minimumAge, CancellationToken token = default)
    {
        if (!Directory.Exists(DirectoryPath)) return Task.CompletedTask;
        var referenced = referencedFileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = timeProvider.GetUtcNow() - minimumAge;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (referenced.Contains(file.Name) || GetPath(file.Name, requireExists: false) is null
                || file.LastWriteTimeUtc >= cutoff.UtcDateTime) continue;
            file.Delete();
        }
        return Task.CompletedTask;
    }

    private string StageContentPath(Guid token) => Path.Combine(StagingDirectory, $"{token:N}.upload");
    private string StageMetadataPath(Guid token) => Path.Combine(StagingDirectory, $"{token:N}.json");
    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
    private static string SafeName(string value)
    {
        var result = Path.GetFileName(value).Trim();
        if (result.Length == 0) result = "referencia";
        return result[..Math.Min(160, result.Length)];
    }

    private static ImageFormat? Detect(byte[] data)
    {
        if (data.Length >= 45 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return new("png", "image/png", BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)));
        if (data.Length >= 12 && data[0] == 0xff && data[1] == 0xd8 && data[^2] == 0xff && data[^1] == 0xd9)
        {
            for (var offset = 2; offset + 8 < data.Length;)
            {
                if (data[offset++] != 0xff) return null;
                var marker = data[offset++];
                if (marker is 0xd8 or 0xd9) continue;
                if (offset + 2 > data.Length) return null;
                var length = (data[offset] << 8) + data[offset + 1];
                if (length < 2 || offset + length > data.Length) return null;
                if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
                    return new("jpg", "image/jpeg", (data[offset + 5] << 8) + data[offset + 6],
                        (data[offset + 3] << 8) + data[offset + 4]);
                offset += length;
            }
        }
        if (data.Length >= 30 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            if (data.AsSpan(12, 4).SequenceEqual("VP8X"u8))
                return new("webp", "image/webp", 1 + ReadUInt24(data.AsSpan(24, 3)),
                    1 + ReadUInt24(data.AsSpan(27, 3)));
            if (data.AsSpan(12, 4).SequenceEqual("VP8L"u8) && data[20] == 0x2f)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(21, 4));
                return new("webp", "image/webp", (int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
            }
            if (data.AsSpan(12, 4).SequenceEqual("VP8 "u8) && data.AsSpan(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                return new("webp", "image/webp", BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26, 2)) & 0x3fff,
                    BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28, 2)) & 0x3fff);
        }
        return null;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    private static string ResolveDirectory(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["WarehouseMap:ReferenceStorageDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException("WarehouseMap:ReferenceStorageDirectory debe ser una ruta absoluta.");
            var resolved = Path.GetFullPath(configured);
            if (environment.IsProduction() && !resolved.StartsWith(ProductionRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("El fondo del croquis debe permanecer dentro de C:\\ProgramData\\WarehouseEPI.");
            return resolved;
        }
        return environment.IsProduction()
            ? Path.Combine(@"C:\ProgramData\WarehouseEPI", "WarehouseMapReferences")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WarehouseEPI", "WarehouseMapReferences");
    }

    private sealed record ImageFormat(string Extension, string ContentType, int Width, int Height);
}
