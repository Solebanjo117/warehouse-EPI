using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class AdjustmentModel(
    InventoryMovementService movementService,
    InventoryQueryService inventoryQuery,
    OperationalInventoryQueryService operationalQuery)
    : OperationPageModel(movementService, inventoryQuery, operationalQuery)
{
    public override InventoryMovementType MovementType => InventoryMovementType.Adjustment;
    public override string PageTitle => "Ajuste por conteo";
    public override string PageHelp => "Registra el conteo físico final; el sistema calculará la diferencia.";
}
