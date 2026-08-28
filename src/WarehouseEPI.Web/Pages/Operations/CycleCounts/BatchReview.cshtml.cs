using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class BatchReviewModel(CycleCountService cycleCounts) : PageModel
{
    public CycleCountCampaignDetail? Campaign { get; private set; }
    public List<ReviewInput> Decisions { get; set; } = [];
    [BindProperty] public InputModel Input { get; set; } = new();
    public string? Error { get; private set; }

    /// <summary>
    /// Conserva el identificador de idempotencia entre renders: regenerarlo en cada intento
    /// permitiría registrar dos veces la misma autorización de ajuste.
    /// </summary>
    public Guid DecisionOperationId(int index) =>
        index < Input.Decisions.Count && Input.Decisions[index].OperationId != Guid.Empty
            ? Input.Decisions[index].OperationId
            : Guid.NewGuid();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token) { Campaign = await cycleCounts.GetCampaignAsync(id, token); if (Campaign is null) return NotFound(); await LoadAsync(token); return Page(); }
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken token)
    {
        var commands = Input.Decisions.Select(item => new CycleCountReviewDecisionCommand(item.LocationId, item.OperationId, item.Decision, item.Reason, item.Notes)).ToArray();
        var result = await cycleCounts.ReviewBatchAsync(new(id, Input.OperationId, Input.Pin, commands), token);
        if (result.Status == CycleCountStatus.Success) return RedirectToPage("Details", new { id });
        Campaign = await cycleCounts.GetCampaignAsync(id, token); Error = CycleCountPresentation.StatusMessage(result); await LoadAsync(token); return Page();
    }
    private async Task LoadAsync(CancellationToken token)
    {
        if (Campaign is null) return; Decisions = [];
        foreach (var location in Campaign.Locations.Where(item => item.Status == CycleCountLocationStatus.UnderReview))
        { var attempt = await cycleCounts.GetLatestAttemptAsync(location.Id, true, token); Decisions.Add(new(location.Id, location.LocationCode, attempt?.Entries.Where(item => item.Difference != 0).ToArray() ?? [])); }
    }
    public sealed record ReviewInput(Guid LocationId, string LocationCode, IReadOnlyList<CycleCountEntryItem> Entries);
    public sealed class InputModel { public Guid OperationId { get; set; } public string Pin { get; set; } = string.Empty; public List<DecisionInput> Decisions { get; set; } = []; }
    public sealed class DecisionInput { public Guid LocationId { get; set; } public Guid OperationId { get; set; } public CycleCountReviewDecision Decision { get; set; } public CycleCountAdjustmentReason? Reason { get; set; } public string? Notes { get; set; } }
}
