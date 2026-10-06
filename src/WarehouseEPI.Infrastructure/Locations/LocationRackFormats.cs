using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Locations;

public static class LocationRackFormats
{
    public static async Task<RackFormat> GetAsync(WarehouseDbContext db, string rowCode,
        short rackNumber, CancellationToken token = default)
    {
        var stored = await db.LocationRackFormats.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RowCode == rowCode && item.RackNumber == rackNumber, token);
        return stored is null ? RackFormat.Default : new(stored.Columns, stored.Levels);
    }

    // One query for all configured racks; unconfigured racks retain the standard 3 x 3 elevation.
    public static async Task<IReadOnlyDictionary<(string RowCode, short RackNumber), RackFormat>> LoadAsync(
        WarehouseDbContext db, CancellationToken token = default)
        => (await db.LocationRackFormats.AsNoTracking().ToListAsync(token))
            .ToDictionary(item => (item.RowCode, item.RackNumber), item => new RackFormat(item.Columns, item.Levels));
}
