using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Locations;

public sealed class LocationLookupService(WarehouseDbContext dbContext)
{
    public Task<Location?> FindByCodeAsync(string? scannedCode, CancellationToken cancellationToken = default)
    {
        var code = LocationNormalization.NormalizeForLookup(scannedCode);
        return dbContext.Locations.AsNoTracking().SingleOrDefaultAsync(
            location => location.Code == code && location.IsPhysicallyPresent, cancellationToken);
    }
}
