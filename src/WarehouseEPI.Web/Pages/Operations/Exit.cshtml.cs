using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class ExitModel(
    InventoryMovementService movementService,
    InventoryQueryService inventoryQuery,
    OperationalInventoryQueryService operationalQuery)
    : OperationPageModel(movementService, inventoryQuery, operationalQuery)
{
    public override InventoryMovementType MovementType => InventoryMovementType.Exit;
    public override string PageTitle => "Salida";
    public override string PageHelp => "Retira material de una ubicación.";
}
