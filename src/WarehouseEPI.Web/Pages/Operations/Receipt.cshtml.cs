using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class ReceiptModel(OperationalInventoryQueryService operationalQuery) : PageModel
{
    public InventoryReceipt Receipt { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await operationalQuery.GetReceiptAsync(id, cancellationToken);
        if (receipt is null)
            return NotFound();
        Receipt = receipt;
        return Page();
    }
}
