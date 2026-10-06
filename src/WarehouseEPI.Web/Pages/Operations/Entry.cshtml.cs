using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class EntryModel(
    InventoryMovementService movementService,
    InventoryQueryService inventoryQuery,
    OperationalInventoryQueryService operationalQuery, IStringLocalizer<OperationsTexts> texts)
    : OperationPageModel(movementService, inventoryQuery, operationalQuery, texts)
{
    public override InventoryMovementType MovementType => InventoryMovementType.Entry;
    public override string PageTitle => T("Entrada");
    public override string PageHelp => T("Registra material recibido en una ubicación.");
}
