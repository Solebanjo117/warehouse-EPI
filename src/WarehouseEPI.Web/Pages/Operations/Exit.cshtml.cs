using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Production;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class ExitModel : OperationPageModel
{
    private readonly ProductionSupplyPreparationService preparations;

    public ExitModel(InventoryMovementService movementService, InventoryQueryService inventoryQuery,
        OperationalInventoryQueryService operationalQuery, ProductionSupplyPreparationService preparations,
        IStringLocalizer<OperationsTexts> texts)
        : base(movementService, inventoryQuery, operationalQuery, texts) => this.preparations = preparations;
    public override InventoryMovementType MovementType => InventoryMovementType.Exit;
    protected override InventoryMovementType CommandMovementType => Input.ExitMode == ExitMode.Wip
        ? InventoryMovementType.Transfer
        : InventoryMovementType.Exit;
    public override InventoryMovementPurpose MovementPurpose => Input.ExitMode == ExitMode.Wip
        ? InventoryMovementPurpose.ProductionIssue
        : InventoryMovementPurpose.GeneralExit;
    public override string PageTitle => T("Salida");
    public override string PageHelp => T("Elige salida general o surtimiento WIP general antes de capturar.");

    public override async Task<IActionResult> OnGetAsync(Guid? productId, Guid? sourceLocationId,
        Guid? destinationLocationId, Guid? locationId, string? mode,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(mode, "wip", StringComparison.OrdinalIgnoreCase))
        {
            var query = PageContext?.HttpContext?.Request.Query;
            var lineId = Guid.TryParse(query?["supplyRequestLineId"].ToString(), out var parsedLineId) ? parsedLineId : (Guid?)null;
            var orderId = Guid.TryParse(query?["workOrderId"].ToString(), out var parsedOrderId) ? parsedOrderId : (Guid?)null;
            var stageId = Guid.TryParse(query?["workOrderStageId"].ToString(), out var parsedStageId) ? parsedStageId : (Guid?)null;
            if (lineId.HasValue || orderId.HasValue || stageId.HasValue)
            {
                var resolved = await preparations.ResolveLegacyLineAsync(lineId, orderId, stageId, productId,
                    cancellationToken);
                if (resolved is Guid resolvedLineId)
                    return RedirectToPage("/Operations/ProductionSupply/Prepare", new { lineId = resolvedLineId });
                return RedirectToPage("/Operations/ProductionSupply/Index");
            }
        }
        return await base.OnGetAsync(productId, sourceLocationId, destinationLocationId, locationId, mode,
            cancellationToken);
    }
}
