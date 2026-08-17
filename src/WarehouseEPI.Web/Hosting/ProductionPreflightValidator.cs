using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Observability;
using WarehouseEPI.Web.Security;

namespace WarehouseEPI.Web.Hosting;

internal static class ProductionPreflightValidator
{
    internal static async Task ValidateAsync(
        IServiceProvider services,
        ProductionSecuritySettings security,
        ObservabilitySettings observability,
        CancellationToken cancellationToken = default)
    {
        _ = security.LoadServerCertificate();
        if (!Directory.Exists(security.DataProtectionKeysPath))
            throw new InvalidOperationException("La ruta de Data Protection no está disponible para el servicio.");
        if (!Directory.Exists(observability.LogDirectory))
            throw new InvalidOperationException("La ruta de logs no está disponible para el servicio.");

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        await ValidateDatabaseAsync(dbContext, cancellationToken);
    }

    internal static async Task ValidateDatabaseAsync(
        WarehouseDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (await dbContext.Database.CanConnectAsync(cancellationToken)) return;
        }
        catch (Exception)
        {
            // El detalle del proveedor puede contener metadatos de conexión.
        }

        throw new InvalidOperationException("La comprobación de conectividad PostgreSQL falló.");
    }
}
