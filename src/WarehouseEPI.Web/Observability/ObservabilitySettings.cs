namespace WarehouseEPI.Web.Observability;

internal sealed class ObservabilitySettings
{
    internal const string SectionName = "Observability";
    internal const string DefaultLogDirectory = @"C:\ProgramData\WarehouseEPI\Logs";

    public required string LogDirectory { get; init; }
    public required int RetentionDays { get; init; }
    public required long FileSizeLimitBytes { get; init; }

    internal static ObservabilitySettings Load(IConfiguration configuration, bool validateProduction)
    {
        var section = configuration.GetSection(SectionName);
        var directory = section["LogDirectory"] ?? DefaultLogDirectory;
        var retentionDays = section.GetValue<int?>("RetentionDays") ?? 30;
        var limitMegabytes = section.GetValue<int?>("FileSizeLimitMegabytes") ?? 50;

        if (retentionDays is < 1 or > 365 || limitMegabytes is < 1 or > 500)
            throw new InvalidOperationException("Los límites de Observability no son válidos.");

        if (!validateProduction)
            return new() { LogDirectory = directory, RetentionDays = retentionDays, FileSizeLimitBytes = limitMegabytes * 1024L * 1024L };

        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Observability:LogDirectory debe ser una ruta absoluta en producción.");
        if (!Directory.Exists(directory))
            throw new InvalidOperationException("Observability:LogDirectory debe existir antes de iniciar producción.");

        var probe = Path.Combine(directory, $".warehouse-epi-write-{Guid.NewGuid():N}");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException("Observability:LogDirectory no permite escritura para la cuenta de la aplicación.", exception);
        }

        return new() { LogDirectory = directory, RetentionDays = retentionDays, FileSizeLimitBytes = limitMegabytes * 1024L * 1024L };
    }
}
