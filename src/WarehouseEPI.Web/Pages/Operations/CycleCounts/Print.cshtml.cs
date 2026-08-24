using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class PrintModel(CycleCountService cycleCountService) : PageModel
{
    public CycleCountCampaignDetail? Campaign { get; private set; }
    public CycleCountLocationItem? Location { get; private set; }
    public CycleCountAttemptView? Attempt { get; private set; }
    public async Task OnGetAsync(Guid id, Guid locationId, Guid attemptId, CancellationToken cancellationToken)
    { Campaign=await cycleCountService.GetCampaignAsync(id,cancellationToken); Location=Campaign?.Locations.SingleOrDefault(item=>item.Id==locationId); Attempt=await cycleCountService.GetAttemptAsync(attemptId,false,cancellationToken); }
}
