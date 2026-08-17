namespace WarehouseEPI.Web.Hosting;

internal static class ServiceConfigurationLoader
{
    internal const string ConfigurationKey = "ServiceConfigPath";
    internal const string DefaultConfigurationDirectory = @"C:\ProgramData\WarehouseEPI\Config";

    internal static string? AddIfConfigured(ConfigurationManager configuration)
    {
        var configuredPath = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;

        var resolvedPath = ValidatePath(configuredPath);
        if (!File.Exists(resolvedPath))
            throw new InvalidOperationException("El archivo protegido de configuración del servicio no existe.");

        configuration.AddJsonFile(resolvedPath, optional: false, reloadOnChange: false);
        return resolvedPath;
    }

    internal static string ValidatePath(string configuredPath)
    {
        if (!Path.IsPathFullyQualified(configuredPath))
            throw new InvalidOperationException("ServiceConfigPath debe ser una ruta absoluta.");

        var resolvedPath = Path.GetFullPath(configuredPath);
        var allowedDirectory = Path.GetFullPath(DefaultConfigurationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolvedPath.StartsWith(allowedDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(resolvedPath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ServiceConfigPath debe ser un archivo JSON dentro de C:\\ProgramData\\WarehouseEPI\\Config.");

        return resolvedPath;
    }
}
