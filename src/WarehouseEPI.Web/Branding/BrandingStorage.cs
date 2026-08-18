using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace WarehouseEPI.Web.Branding;

public sealed class BrandingStorage(IWebHostEnvironment environment, IConfiguration configuration)
{
    public const long MaxLogoBytes = 1024 * 1024;
    private static readonly string ProductionRoot = Path.GetFullPath(@"C:\ProgramData\WarehouseEPI") + Path.DirectorySeparatorChar;

    public string DirectoryPath { get; } = ResolveDirectory(environment, configuration);

    public async Task<StoredLogo> SaveAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaxLogoBytes)
            throw new BrandingValidationException("El logo debe medir entre 1 byte y 1 MB.");

        await using var source = file.OpenReadStream();
        var header = new byte[16];
        var bytesRead = await source.ReadAsync(header.AsMemory(), cancellationToken);
        var format = DetectFormat(header.AsSpan(0, bytesRead));
        if (format is null)
            throw new BrandingValidationException("Use una imagen PNG, JPEG o WebP válida.");

        Directory.CreateDirectory(DirectoryPath);
        var fileName = $"{Guid.NewGuid():N}.{format.Extension}";
        var temporaryPath = Path.Combine(DirectoryPath, $".{fileName}.upload");
        var finalPath = Path.Combine(DirectoryPath, fileName);
        try
        {
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, bufferSize: 81920, useAsync: true))
            {
                await destination.WriteAsync(header.AsMemory(0, bytesRead), cancellationToken);
                await source.CopyToAsync(destination, cancellationToken);
            }

            await using var hashedFile = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashedFile, cancellationToken)).ToLowerInvariant();
            File.Move(temporaryPath, finalPath);
            return new(fileName, format.ContentType, hash);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            throw;
        }
    }

    public string? GetPath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !System.Text.RegularExpressions.Regex.IsMatch(fileName, "^[a-f0-9]{32}\\.(png|jpg|webp)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return null;
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, fileName));
        var root = Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    public void Delete(string? fileName)
    {
        var path = GetPath(fileName);
        if (path is not null) File.Delete(path);
    }

    private static LogoFormat? DetectFormat(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            ? new("png", "image/png")
            : header.Length >= 3 && header[..3].SequenceEqual(new byte[] { 255, 216, 255 })
                ? new("jpg", "image/jpeg")
                : header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
                    ? new("webp", "image/webp")
                    : null;

    private static string ResolveDirectory(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["Branding:StorageDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException("Branding:StorageDirectory debe ser una ruta absoluta.");
            var resolved = Path.GetFullPath(configured);
            if (environment.IsProduction() && !resolved.StartsWith(ProductionRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Branding:StorageDirectory debe permanecer dentro de C:\\ProgramData\\WarehouseEPI en producción.");
            return resolved;
        }
        return environment.IsProduction()
            ? Path.Combine(@"C:\ProgramData\WarehouseEPI", "Branding")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarehouseEPI", "Branding");
    }

    private sealed record LogoFormat(string Extension, string ContentType);
}

public sealed record StoredLogo(string FileName, string ContentType, string Hash);
public sealed class BrandingValidationException(string message) : Exception(message);
