using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class AdjustmentModel(
    InventoryMovementService movementService,
    InventoryQueryService inventoryQuery,
    OperationalInventoryQueryService operationalQuery, IStringLocalizer<OperationsTexts> texts)
    : OperationPageModel(movementService, inventoryQuery, operationalQuery, texts)
{
    public override InventoryMovementType MovementType => InventoryMovementType.Adjustment;
    public override string PageTitle => T("Ajuste por conteo");
    public override string PageHelp => T("Registra el conteo físico final; el sistema calculará la diferencia.");
}
