using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Locations;

public sealed record RackWipAssociationView
{
    public required string RowCode { get; init; }
    public short RackNumber { get; init; }
    public Guid WipAreaId { get; init; }
    public required string Code { get; init; }
    public bool IsActive { get; init; }
    public bool IsBlocked { get; init; }
    public bool IsPhysicallyPresent { get; init; }
    public bool IsOperational => IsActive && !IsBlocked && IsPhysicallyPresent;
}

public static class LocationRackWipAssociations
{
    public static IQueryable<RackWipAssociationView> Query(WarehouseDbContext db) =>
        db.LocationRackWipAssociations.AsNoTracking().Select(x => new RackWipAssociationView
        {
            RowCode = x.RowCode,
            RackNumber = x.RackNumber,
            WipAreaId = x.WipAreaId,
            Code = x.WipArea.Code,
            IsActive = x.WipArea.IsActive,
            IsBlocked = x.WipArea.IsBlocked,
            IsPhysicallyPresent = x.WipArea.IsPhysicallyPresent
        });

    public static Task<RackWipAssociationView?> GetAsync(WarehouseDbContext db, string row, short rack,
        CancellationToken token = default) => Query(db).SingleOrDefaultAsync(x => x.RowCode == row && x.RackNumber == rack, token);

    public static async Task<IReadOnlyDictionary<(string, short), RackWipAssociationView>> LoadAsync(
        WarehouseDbContext db, CancellationToken token = default) =>
        (await Query(db).ToListAsync(token)).ToDictionary(x => (x.RowCode, x.RackNumber));
}
