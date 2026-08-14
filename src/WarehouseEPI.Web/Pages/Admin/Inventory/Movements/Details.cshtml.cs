using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Movements;

[Authorize(Policy = "AdminOnly")]
public sealed class DetailsModel(InventoryHistoryService history) : PageModel
{
    public InventoryMovementDetail Movement { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        var movement = await history.GetAsync(id, token);
        if (movement is null) return NotFound();
        Movement = movement; return Page();
    }
}
