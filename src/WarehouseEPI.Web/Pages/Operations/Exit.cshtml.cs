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
    public override InventoryMovementPurpose MovementPurpose => Input.ExitMode == ExitMode.Wip
        ? InventoryMovementPurpose.ProductionIssue
        : InventoryMovementPurpose.GeneralExit;
    public override string PageTitle => "Salida";
    public override string PageHelp => "Elige salida general o surtimiento a producción antes de capturar.";

}
