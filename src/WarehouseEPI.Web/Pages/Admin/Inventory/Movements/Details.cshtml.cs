using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Movements;

[Authorize(Policy = "AdminOnly")]
public sealed class DetailsModel(InventoryHistoryService history, WarehouseClock clock) : PageModel
{
    public InventoryMovementDetail Movement { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
    public string ReturnUrl { get; private set; } = "/Admin/Inventory/Movements";
    public async Task<IActionResult> OnGetAsync(Guid id, string? returnUrl, CancellationToken token)
    {
        var movement = await history.GetAsync(id, token);
        if (movement is null) return NotFound();
        Movement = movement;
        OccurredAt = await clock.ConvertAsync(movement.OccurredAt, token);
        RecordedAt = await clock.ConvertAsync(movement.RecordedAt, token);
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Page("Index") ?? "/Admin/Inventory/Movements";
        return Page();
    }
}
