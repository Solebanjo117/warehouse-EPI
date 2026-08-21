using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class IndexModel(CycleCountService cycleCountService, WarehouseClock clock) : PageModel
{
    public IReadOnlyList<CycleCountCampaignListItem> Campaigns { get; private set; } = [];
    public CycleCountCampaignStatus? Status { get; private set; }
    public string? Search { get; private set; }
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }

    public async Task OnGetAsync(string? status, string? search, DateOnly? from, DateOnly? to, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        Status = Enum.TryParse<CycleCountCampaignStatus>(status, true, out var parsed) ? parsed : null;
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        From = from;
        To = to;
        var interval = await clock.GetUtcIntervalAsync(from, to, cancellationToken);
        Campaigns = await cycleCountService.GetCampaignsAsync(Status, Search, Math.Max(1, pageNumber), 25, interval.FromInclusive, interval.ToExclusive, cancellationToken);
    }
}
