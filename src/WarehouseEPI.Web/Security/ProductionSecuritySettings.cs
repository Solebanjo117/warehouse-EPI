using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace WarehouseEPI.Web.Security;

internal sealed class ProductionSecuritySettings
{
    internal const string SectionName = "Security";

    public required string DataProtectionKeysPath { get; init; }

    public required string ServerCertificateThumbprint { get; init; }

    public required string AllowedHosts { get; init; }

    public required SecurityRateLimitSettings RateLimits { get; init; }

    internal static ProductionSecuritySettings Load(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var keysPath = section["DataProtectionKeysPath"];
        var thumbprint = section["ServerCertificateThumbprint"];
        var allowedHosts = configuration["AllowedHosts"];
        var rateLimits = new SecurityRateLimitSettings
        {
            AdminLoginPermitLimit = section.GetValue<int?>("RateLimits:AdminLoginPermitLimit") ?? 5,
            AdminLoginWindowMinutes = section.GetValue<int?>("RateLimits:AdminLoginWindowMinutes") ?? 5,
            AdminPostPermitLimit = section.GetValue<int?>("RateLimits:AdminPostPermitLimit") ?? 10,
            AdminPostWindowMinutes = section.GetValue<int?>("RateLimits:AdminPostWindowMinutes") ?? 1,
            OperationPostPermitLimit = section.GetValue<int?>("RateLimits:OperationPostPermitLimit") ?? 30,
            OperationPostWindowMinutes = section.GetValue<int?>("RateLimits:OperationPostWindowMinutes") ?? 1
        };

        if (string.IsNullOrWhiteSpace(keysPath) || !Path.IsPathFullyQualified(keysPath))
            throw new InvalidOperationException("Security:DataProtectionKeysPath debe ser una ruta absoluta en producción.");
        if (!Directory.Exists(keysPath))
            throw new InvalidOperationException("Security:DataProtectionKeysPath debe existir antes de iniciar producción.");
        if (string.IsNullOrWhiteSpace(thumbprint))
            throw new InvalidOperationException("Falta Security:ServerCertificateThumbprint en producción.");
        if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Contains('*', StringComparison.Ordinal))
            throw new InvalidOperationException("AllowedHosts debe incluir solo el nombre e IP reservada del servidor en producción.");
        rateLimits.Validate();

        return new()
        {
            DataProtectionKeysPath = keysPath,
            ServerCertificateThumbprint = NormalizeThumbprint(thumbprint),
            AllowedHosts = allowedHosts,
            RateLimits = rateLimits
        };
    }

    internal X509Certificate2 LoadServerCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certificate = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            ServerCertificateThumbprint,
            validOnly: false).OfType<X509Certificate2>().SingleOrDefault();
        if (certificate is null || !certificate.HasPrivateKey || certificate.NotAfter <= DateTime.Now)
            throw new InvalidOperationException("El certificado HTTPS de Warehouse EPI no está disponible o no es válido.");

        return certificate;
    }

    internal static SslProtocols SupportedTlsProtocols => SslProtocols.Tls12 | SslProtocols.Tls13;

    private static string NormalizeThumbprint(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}

internal sealed class SecurityRateLimitSettings
{
    public int AdminLoginPermitLimit { get; init; }

    public int AdminLoginWindowMinutes { get; init; }

    public int AdminPostPermitLimit { get; init; }

    public int AdminPostWindowMinutes { get; init; }

    public int OperationPostPermitLimit { get; init; }

    public int OperationPostWindowMinutes { get; init; }

    internal void Validate()
    {
        if (AdminLoginPermitLimit is <= 0 or > 100 || AdminLoginWindowMinutes is <= 0 or > 60 ||
            AdminPostPermitLimit is <= 0 or > 1_000 || AdminPostWindowMinutes is <= 0 or > 60 ||
            OperationPostPermitLimit is <= 0 or > 10_000 || OperationPostWindowMinutes is <= 0 or > 60)
            throw new InvalidOperationException("Los límites de solicitudes de Security:RateLimits no son válidos.");
    }
}
