using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class ReviewModel(CycleCountService cycleCountService) : PageModel
{
    public CycleCountCampaignDetail? Campaign { get; private set; }
    public CycleCountAttemptView? Attempt { get; private set; }
    public CycleCountLocationItem? Location { get; private set; }
    [BindProperty] public string Pin { get; set; } = string.Empty;
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public Guid OperationId { get; set; }
    [BindProperty] public List<string> SharedApprovals { get; set; } = [];
    public IReadOnlyList<SharedLocationConflict> SharingConflicts { get; private set; } = [];
    public string? Error { get; private set; }
    public bool Registered { get; private set; }
    public async Task<IActionResult> OnGetAsync(Guid id, Guid locationId, bool registered, CancellationToken cancellationToken) { Registered = registered; await LoadAsync(id, locationId, cancellationToken); return Campaign is null || Location is null ? NotFound() : Page(); }
    public async Task<IActionResult> OnPostRecountAsync(Guid id, Guid locationId, CancellationToken cancellationToken)
    { var result = await cycleCountService.RequestRecountAsync(new(locationId, OperationId == Guid.Empty ? Guid.NewGuid() : OperationId, Pin, Notes), cancellationToken); if (result.Status == CycleCountStatus.Success) return RedirectToPage("Details", new { id }); await LoadAsync(id, locationId, cancellationToken); Error = CycleCountPresentation.StatusMessage(result); return Page(); }
    public async Task<IActionResult> OnPostApproveAsync(Guid id, Guid locationId, CancellationToken cancellationToken)
    {
        var approvals = SharedApprovals.Select(ParseApproval).Where(item => item is not null).Cast<SharedAssignmentApproval>().ToArray();
        var result = await cycleCountService.ApproveAsync(new(locationId, OperationId, Pin, Notes, approvals), cancellationToken);
        if (result.Status == CycleCountStatus.Success) return RedirectToPage("Details", new { id });
        await LoadAsync(id, locationId, cancellationToken);
        SharingConflicts = result.Conflicts;
        Error = CycleCountPresentation.StatusMessage(result);
        return Page();
    }
    private async Task LoadAsync(Guid id, Guid locationId, CancellationToken cancellationToken) { Campaign = await cycleCountService.GetCampaignAsync(id, cancellationToken); Location = Campaign?.Locations.SingleOrDefault(item => item.Id == locationId); if (Location is null) return; Attempt = await cycleCountService.GetLatestAttemptAsync(locationId, true, cancellationToken); }
    private static SharedAssignmentApproval? ParseApproval(string value)
    {
        var parts = value.Split('|');
        return parts.Length == 2 && Guid.TryParse(parts[0], out var productId) && Guid.TryParse(parts[1], out var locationId)
            ? new(productId, locationId)
            : null;
    }
}
