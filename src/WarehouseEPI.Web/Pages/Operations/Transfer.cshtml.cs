using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class TransferModel(
    InventoryMovementService movementService,
    InventoryQueryService inventoryQuery,
    OperationalInventoryQueryService operationalQuery)
    : OperationPageModel(movementService, inventoryQuery, operationalQuery)
{
    public override InventoryMovementType MovementType => InventoryMovementType.Transfer;
    public override string PageTitle => "Transferencia";
    public override string PageHelp => "Mueve material entre dos ubicaciones en una sola operación.";
}
