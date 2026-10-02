namespace WarehouseEPI.Web.Backups;

public sealed class ManualBackupSettings(IWebHostEnvironment environment, IConfiguration configuration)
{
    public string DirectoryPath { get; } = configuration["Backups:ManualDirectory"] ??
        Path.Combine(environment.IsProduction() ? @"C:\ProgramData\WarehouseEPI" :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarehouseEPI"), "ManualBackups");
    public string PgDumpPath { get; } = configuration["Backups:PgDumpPath"] ?? @"C:\Program Files\PostgreSQL\18\bin\pg_dump.exe";
    public string PgRestorePath { get; } = configuration["Backups:PgRestorePath"] ?? @"C:\Program Files\PostgreSQL\18\bin\pg_restore.exe";
    public string PinLookupKey { get; } = configuration["Security:PinLookupKey"] ?? "";

    public bool HasSafeStorageDirectory()
    {
        try
        {
            if (!Path.IsPathFullyQualified(DirectoryPath)) return false;
            var resolved = Path.GetFullPath(DirectoryPath);
            var productionRoot = Path.GetFullPath(@"C:\ProgramData\WarehouseEPI\ManualBackups").TrimEnd(Path.DirectorySeparatorChar);
            if (environment.IsProduction() && !resolved.Equals(productionRoot, StringComparison.OrdinalIgnoreCase) &&
                !resolved.StartsWith(productionRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            for (var parent = new DirectoryInfo(resolved); parent is not null; parent = parent.Parent)
                if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public string? GetUnavailableReason()
    {
        try
        {
            if (!HasSafeStorageDirectory())
                return "IT debe configurar la carpeta privada de respaldos manuales.";
            if (!Path.IsPathFullyQualified(PgDumpPath) || !Path.IsPathFullyQualified(PgRestorePath) ||
                !File.Exists(PgDumpPath) || !File.Exists(PgRestorePath))
                return "IT debe instalar las herramientas de PostgreSQL 18 en el servidor.";
            if (Convert.FromBase64String(PinLookupKey).Length < 32)
                return "IT debe revisar la configuración de la clave de los NIP.";
            if (!environment.IsProduction()) Directory.CreateDirectory(DirectoryPath);
            if (!Directory.Exists(DirectoryPath))
                return "IT debe habilitar los permisos de respaldos manuales para el servicio.";
            var probe = Path.Combine(DirectoryPath, $".probe-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return "IT debe revisar los permisos y la configuración de respaldos manuales.";
        }
    }
}
