using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Locations;

public static class LocationWipRules
{
    public static async Task<string?> ValidateChangeAsync(WarehouseDbContext db, Guid locationId,
        LocationOperationalRole current, LocationOperationalRole requested, CancellationToken token)
    {
        if (current == requested) return null;
        if (await db.LocationRackWipAssociations.AnyAsync(x => x.WipAreaId == locationId, token))
            return "Desconecta los racks asociados antes de cambiar la función del área WIP.";
        if (db.Database.CurrentTransaction is { } tx)
            await InventoryMovementStore.LockLocationsAsync([locationId], tx, token);
        if (requested == LocationOperationalRole.Wip &&
            (await db.InventoryBalances.AnyAsync(x => x.LocationId == locationId && x.Quantity != 0, token) ||
             await db.PalletPlates.AnyAsync(x => x.LocationId == locationId && !x.IsVoided && x.Quantity != 0, token) ||
             await db.ProductLocationAssignments.AnyAsync(x => x.LocationId == locationId && x.IsActive, token) ||
             await db.ProductionWarehouseReservations.AnyAsync(x => x.LocationId == locationId && x.Quantity > x.ReleasedQuantity, token)))
            return "Para cambiar a WIP, retira el saldo, las placas con material, las reservas y las asignaciones de almacenamiento.";
        if (current == LocationOperationalRole.Wip && await db.WipDocuments.AnyAsync(x => x.WipLocationId == locationId && !x.IsCancelled &&
            x.Quantity > x.Applications.Where(a => a.Kind != WipDocumentApplicationKind.Reversal &&
                !db.WipDocumentApplications.Any(r => r.ReversesApplicationId == a.Id)).Sum(a => a.Quantity), token))
            return "Resuelve el seguimiento documental pendiente antes de cambiar WIP a almacenamiento.";
        return null;
    }
}
