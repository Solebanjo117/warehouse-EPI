using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Admin.Inventory;

[Authorize(Policy = "AdminOnly")]
public sealed class AlertsModel(InventoryQueryService inventory) : PageModel
{
    public IReadOnlyList<InventoryBalanceView> NegativeBalances { get; private set; } = [];
    public IReadOnlyList<ProductStockSummary> BelowMinimum { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken token) { NegativeBalances = await inventory.GetNegativeBalancesAsync(token); BelowMinimum = await inventory.GetBelowMinimumProductsAsync(token); }
}
