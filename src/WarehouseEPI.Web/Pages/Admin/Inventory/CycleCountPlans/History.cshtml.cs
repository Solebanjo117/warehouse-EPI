using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.CycleCountPlans;

[Authorize(Policy = "AdminOnly")]
public sealed class HistoryModel(CycleCountService cycleCounts, WarehouseClock warehouseClock) : PageModel
{
    public CycleCountPlanCatalogItem? Plan { get; private set; }
    public PagedResult<HistoryRow> Result { get; private set; } = new([], 0, 1, 25);

    public async Task<IActionResult> OnGetAsync(Guid id, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        Plan = await cycleCounts.GetPlanAsync(id, cancellationToken);
        if (Plan is null) return NotFound();
        var events = await cycleCounts.GetPlanEventsAsync(id, pageNumber, 25, cancellationToken);
        var rows = new List<HistoryRow>(events.Items.Count);
        foreach (var item in events.Items)
            rows.Add(new(item, await warehouseClock.ConvertAsync(item.RecordedAt, cancellationToken)));
        Result = new(rows, events.Total, events.Page, events.PageSize);
        return Page();
    }

    public sealed record HistoryRow(CycleCountPlanEventItem Event, DateTimeOffset LocalDate);
}
