using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class DetailsModel(CycleCountService cycleCountService) : PageModel
{
    public CycleCountCampaignDetail? Campaign { get; private set; }
    [BindProperty] public string Pin { get; set; } = string.Empty;
    [BindProperty] public string? Notes { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) { Campaign = await cycleCountService.GetCampaignAsync(id, cancellationToken); return Campaign is null ? NotFound() : Page(); }
    public async Task<IActionResult> OnPostReleaseAsync(Guid id, Guid operationId, CancellationToken cancellationToken) => await ExecuteAsync(id, () => cycleCountService.ReleaseAsync(id, operationId == Guid.Empty ? Guid.NewGuid() : operationId, Pin, cancellationToken), cancellationToken);
    public async Task<IActionResult> OnPostCancelAsync(Guid id, Guid operationId, CancellationToken cancellationToken) => await ExecuteAsync(id, () => cycleCountService.CancelAsync(id, operationId == Guid.Empty ? Guid.NewGuid() : operationId, Pin, Notes, cancellationToken), cancellationToken);
    public async Task<IActionResult> OnPostStartAsync(Guid id, Guid locationId, Guid operationId, CancellationToken cancellationToken)
    {
        var result = await cycleCountService.StartAttemptAsync(locationId, operationId, Pin, cancellationToken);
        if (result.Status == CycleCountStatus.Success && result.AttemptId is Guid attemptId) return RedirectToPage("Count", new { id, locationId, attemptId });
        Campaign = await cycleCountService.GetCampaignAsync(id, cancellationToken); Error = result.Status == CycleCountStatus.InvalidPin ? "NIP no válido." : string.Join(' ', result.ValidationErrors); return Page();
    }
    private async Task<IActionResult> ExecuteAsync(Guid id, Func<Task<CycleCountResult>> action, CancellationToken cancellationToken)
    { var result = await action(); if (result.Status == CycleCountStatus.Success) return RedirectToPage(new { id }); Campaign = await cycleCountService.GetCampaignAsync(id, cancellationToken); Error = result.Status == CycleCountStatus.InvalidPin ? "NIP no válido." : string.Join(' ', result.ValidationErrors); return Page(); }
}
