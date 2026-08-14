using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class EntryModel(
    InventoryMovementService movementService,
    InventoryQueryService inventoryQuery,
    OperationalInventoryQueryService operationalQuery)
    : OperationPageModel(movementService, inventoryQuery, operationalQuery)
{
    public override InventoryMovementType MovementType => InventoryMovementType.Entry;
    public override string PageTitle => "Entrada";
    public override string PageHelp => "Registra material recibido en una ubicación.";
}
