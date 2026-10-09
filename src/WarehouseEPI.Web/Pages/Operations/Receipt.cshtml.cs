using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class ReceiptModel(OperationalInventoryQueryService operationalQuery, ReceivingQueryService receivingQuery, WarehouseClock warehouseClock, ProductionQueryService productionQuery, StagingArrivalQuery staging) : PageModel
{
    public ProductionReceiptLink? Production { get; private set; }
    public InventoryReceipt Receipt { get; private set; } = null!;
    public IReadOnlyDictionary<Guid, StagingArrivalRow> StagingArrivals { get; private set; } = new Dictionary<Guid, StagingArrivalRow>();
    public ReceivingMovementDocumentLink? ReceivingDocument { get; private set; }
    public DateTimeOffset OccurredAtWarehouseTime { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await operationalQuery.GetReceiptAsync(id, cancellationToken);
        if (receipt is null)
            return NotFound();
        Receipt = receipt;
        if (receipt.Type == InventoryMovementType.Entry && receipt.Purpose is InventoryMovementPurpose.Standard
            or InventoryMovementPurpose.DocumentReceipt or InventoryMovementPurpose.ProductionReceipt)
            StagingArrivals = (await staging.ForMovementAsync(id, cancellationToken)).ToDictionary(row => row.LineId);
        if (receipt.Purpose == InventoryMovementPurpose.ProductionReceipt) Production = await productionQuery.GetReceiptLinkAsync(id, cancellationToken);
        ReceivingDocument = await receivingQuery.GetMovementLinkAsync(id, cancellationToken);
        OccurredAtWarehouseTime = await warehouseClock.ConvertAsync(receipt.OccurredAt, cancellationToken);
        return Page();
    }
}
