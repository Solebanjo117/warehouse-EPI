using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class ExitModel : OperationPageModel
{
    private readonly ProductionMaterialService? productionMaterials;

    public ExitModel(InventoryMovementService movementService, InventoryQueryService inventoryQuery,
        OperationalInventoryQueryService operationalQuery, ProductionMaterialService? productionMaterials = null)
        : base(movementService, inventoryQuery, operationalQuery, productionMaterials)
    {
        this.productionMaterials = productionMaterials;
    }
    public override InventoryMovementType MovementType => InventoryMovementType.Exit;
    protected override InventoryMovementType CommandMovementType => Input.ExitMode == ExitMode.Wip
        ? InventoryMovementType.Transfer
        : InventoryMovementType.Exit;
    public override InventoryMovementPurpose MovementPurpose => Input.ExitMode == ExitMode.Wip
        ? InventoryMovementPurpose.ProductionIssue
        : InventoryMovementPurpose.GeneralExit;
    public override string PageTitle => "Salida";
    public override string PageHelp => "Elige salida general o surtimiento a producción antes de capturar.";

    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnGetProductionTargetsAsync(Guid destinationId,
        string? q, CancellationToken token) => new Microsoft.AspNetCore.Mvc.JsonResult(productionMaterials is null
            ? []
            : await productionMaterials.SearchTargetsAsync(destinationId, q, token));

}
