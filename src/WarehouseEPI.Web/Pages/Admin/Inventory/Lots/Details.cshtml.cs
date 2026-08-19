using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Lots;

public sealed class DetailsModel(ProductLotQueryService lots, WarehouseClock clock) : PageModel
{
    public ProductLotDetail Lot { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(id, cancellationToken);
        if (lot is null) return NotFound();
        Lot = lot; CreatedAt = await clock.ConvertAsync(lot.CreatedAt, cancellationToken); return Page();
    }
}
