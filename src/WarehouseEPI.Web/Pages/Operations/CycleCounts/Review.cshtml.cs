using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class ReviewModel(CycleCountService cycleCountService) : PageModel
{
    public CycleCountCampaignDetail? Campaign { get; private set; }
    public CycleCountAttemptView? Attempt { get; private set; }
    public CycleCountLocationItem? Location { get; private set; }
    public async Task<IActionResult> OnGetAsync(Guid id, Guid locationId, CancellationToken cancellationToken) { await LoadAsync(id, locationId, cancellationToken); return Campaign is null || Location is null ? NotFound() : Page(); }
    private async Task LoadAsync(Guid id, Guid locationId, CancellationToken cancellationToken) { Campaign = await cycleCountService.GetCampaignAsync(id, cancellationToken); Location = Campaign?.Locations.SingleOrDefault(item => item.Id == locationId); if (Location is null) return; Attempt = await cycleCountService.GetLatestAttemptAsync(locationId, true, cancellationToken); }
}
