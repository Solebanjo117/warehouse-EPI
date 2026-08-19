using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory;

[Authorize(Policy = "AdminOnly")]
public sealed class AlertsModel(InventoryQueryService inventory, WarehouseClock warehouseClock) : PageModel
{
    private const int PageSize = 25;

    public string View { get; private set; } = "negative";
    public string? Search { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public InventoryAlertSummary Summary { get; private set; } = new(0, 0, 0);
    public InventoryAlertPage<NegativeInventoryAlert> NegativeAlerts { get; private set; } = new([], 0);
    public InventoryAlertPage<MinimumStockInventoryAlert> MinimumAlerts { get; private set; } = new([], 0);
    public DateTimeOffset UpdatedAt { get; private set; }

    public async Task OnGetAsync(string? view, string? search, int pageNumber = 1, CancellationToken token = default)
    {
        View = view == "minimum" ? "minimum" : "negative";
        Search = search?.Trim();
        Summary = await inventory.GetAlertSummaryAsync(token);
        UpdatedAt = await warehouseClock.ConvertAsync(DateTimeOffset.UtcNow, token);
        if (View == "minimum")
        {
            MinimumAlerts = await inventory.GetBelowMinimumAlertPageAsync(Search, pageNumber, PageSize, token);
            TotalPages = Math.Max(1, (int)Math.Ceiling(MinimumAlerts.TotalCount / (double)PageSize));
        }
        else
        {
            NegativeAlerts = await inventory.GetNegativeAlertPageAsync(Search, pageNumber, PageSize, token);
            TotalPages = Math.Max(1, (int)Math.Ceiling(NegativeAlerts.TotalCount / (double)PageSize));
        }
        PageNumber = Math.Clamp(Math.Max(1, pageNumber), 1, TotalPages);
    }
}
