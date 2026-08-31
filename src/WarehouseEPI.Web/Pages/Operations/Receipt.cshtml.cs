using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class ReceiptModel(OperationalInventoryQueryService operationalQuery, ReceivingQueryService receivingQuery, WarehouseClock warehouseClock) : PageModel
{
    public InventoryReceipt Receipt { get; private set; } = null!;
    public ReceivingMovementDocumentLink? ReceivingDocument { get; private set; }
    public DateTimeOffset OccurredAtWarehouseTime { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await operationalQuery.GetReceiptAsync(id, cancellationToken);
        if (receipt is null)
            return NotFound();
        Receipt = receipt;
        ReceivingDocument = await receivingQuery.GetMovementLinkAsync(id, cancellationToken);
        OccurredAtWarehouseTime = await warehouseClock.ConvertAsync(receipt.OccurredAt, cancellationToken);
        return Page();
    }
}
