using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class ReceiptModel(OperationalInventoryQueryService operationalQuery, WarehouseClock warehouseClock) : PageModel
{
    public InventoryReceipt Receipt { get; private set; } = null!;
    public DateTimeOffset OccurredAtWarehouseTime { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await operationalQuery.GetReceiptAsync(id, cancellationToken);
        if (receipt is null)
            return NotFound();
        Receipt = receipt;
        OccurredAtWarehouseTime = await warehouseClock.ConvertAsync(receipt.OccurredAt, cancellationToken);
        return Page();
    }
}
