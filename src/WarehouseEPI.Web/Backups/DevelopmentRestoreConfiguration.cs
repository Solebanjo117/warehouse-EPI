using Npgsql;

namespace WarehouseEPI.Web.Backups;

public static class DevelopmentRestoreConfiguration
{
    public const string ConfigurationKey = "Development:RestoreConfigPath";

    public static bool Configure(IWebHostEnvironment environment, IConfigurationBuilder configuration)
    {
        var current = configuration.Build();
        var path = current[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("La restauración de desarrollo solo puede habilitarse en Development.");

        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "../..", "artifacts", "local-restore"));
        var expected = Path.Combine(root, "config.json");
        if (!Path.IsPathFullyQualified(path) || !Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use el inicio local para configurar la restauración de desarrollo.");
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("La instalación de desarrollo no puede usar enlaces.");
        if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("La configuración de desarrollo no puede usar enlaces.");

        configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        var settings = configuration.Build();
        var connection = new NpgsqlConnectionStringBuilder(settings.GetConnectionString("Warehouse"));
        if (connection.Host != "127.0.0.1" || connection.Port is < 1025 or > 65535 || connection.Port == 5432 ||
            connection.Port != settings.GetValue<int>("Development:PostgreSqlPort") ||
            connection.Database != "warehouse_epi_restore_dev" || connection.Username != "warehouse_epi_dev_app" ||
            !settings.GetValue<bool>("Development:UseEphemeralDataProtection"))
            throw new InvalidOperationException("La restauración de desarrollo requiere su instancia PostgreSQL aislada.");
        foreach (var (key, folder) in new[] {
            ("Backups:ManualDirectory", "ManualBackups"), ("Branding:StorageDirectory", "Branding"),
            ("WarehouseMap:ReferenceStorageDirectory", "WarehouseMapReferences"), ("Observability:LogDirectory", "Logs") })
        {
            if (!string.Equals(settings[key], Path.Combine(root, folder), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Las carpetas de restauración deben pertenecer a la instalación de desarrollo.");
            var directory = new DirectoryInfo(Path.Combine(root, folder));
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Las carpetas de desarrollo no pueden usar enlaces.");
        }
        return true;
    }
}
