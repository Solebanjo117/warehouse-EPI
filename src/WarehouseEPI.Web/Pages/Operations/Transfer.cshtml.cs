using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class TransferModel(
    InventoryMovementService movementService,
    InventoryQueryService inventoryQuery,
    OperationalInventoryQueryService operationalQuery, IStringLocalizer<OperationsTexts> texts)
    : OperationPageModel(movementService, inventoryQuery, operationalQuery, texts)
{
    public override InventoryMovementType MovementType => InventoryMovementType.Transfer;
    public override string PageTitle => T("Transferencia");
    public override string PageHelp => T("Mueve material entre dos ubicaciones en una sola operación.");
}
