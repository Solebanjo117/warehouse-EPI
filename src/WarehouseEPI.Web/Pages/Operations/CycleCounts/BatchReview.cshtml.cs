using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class BatchReviewModel(CycleCountService cycleCounts, IStringLocalizer<OperationsTexts> texts) : PageModel
{
    public CycleCountCampaignDetail? Campaign { get; private set; }
    public List<ReviewInput> Decisions { get; private set; } = [];
    public IReadOnlyDictionary<Guid, CycleCountBatchItemResult> ItemResults { get; private set; } = new Dictionary<Guid, CycleCountBatchItemResult>();
    [BindProperty] public InputModel Input { get; set; } = new();
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        Campaign = await cycleCounts.GetCampaignAsync(id, token);
        if (Campaign is null) return NotFound();
        await LoadAsync(token);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken token)
    {
        var pin = Input.Pin;
        Input.Pin = string.Empty;
        var commands = Input.Decisions.Select(item => new CycleCountReviewDecisionCommand(
            item.LocationId,
            item.OperationId,
            item.Decision,
            item.Reason,
            item.Notes,
            item.SharedApprovals.Select(ParseApproval).Where(value => value is not null).Cast<SharedAssignmentApproval>().ToArray())).ToArray();
        var result = await cycleCounts.ReviewBatchAsync(new(id, Input.OperationId, pin, commands), token);
        if (result.Status == CycleCountStatus.Success && result.Items.All(item => item.Status == CycleCountStatus.Success))
            return RedirectToPage("Details", new { id });

        Campaign = await cycleCounts.GetCampaignAsync(id, token);
        if (Campaign is null) return NotFound();
        ItemResults = result.Items.ToDictionary(item => item.LocationId);
        Error = result.Status == CycleCountStatus.Success
            ? string.Format(texts["Se aplicaron {0} de {1} decisiones. Revisa las ubicaciones pendientes."].Value, result.Items.Count(item => item.Status == CycleCountStatus.Success), result.Items.Count)
            : CycleCountPresentation.StatusMessage(result, texts);
        if (result.Status == CycleCountStatus.Success) Input.OperationId = Guid.NewGuid();
        await LoadAsync(token);
        return Page();
    }

    public string? ItemMessage(Guid locationId)
    {
        if (!ItemResults.TryGetValue(locationId, out var item) || item.Status == CycleCountStatus.Success) return null;
        return CycleCountPresentation.StatusMessage(new CycleCountResult(item.Status, Errors: item.Errors, SharingConflicts: item.SharingConflicts), texts);
    }

    private async Task LoadAsync(CancellationToken token)
    {
        if (Campaign is null) return;
        var previous = Input.Decisions.GroupBy(item => item.LocationId).ToDictionary(group => group.Key, group => group.First());
        Input.Decisions = [];
        Decisions = [];
        foreach (var location in Campaign.Locations.Where(item => item.Status is CycleCountLocationStatus.UnderReview or CycleCountLocationStatus.Stale))
        {
            var attempt = await cycleCounts.GetLatestAttemptAsync(location.Id, true, token);
            var conflicts = location.Status == CycleCountLocationStatus.UnderReview
                ? await cycleCounts.GetReviewSharingConflictsAsync(location.Id, token)
                : [];
            var input = previous.GetValueOrDefault(location.Id) ?? new DecisionInput
            {
                LocationId = location.Id,
                OperationId = Guid.NewGuid(),
                Decision = location.Status == CycleCountLocationStatus.Stale ? CycleCountReviewDecision.Recount : CycleCountReviewDecision.Approve,
                Reason = location.Status == CycleCountLocationStatus.Stale ? null : CycleCountAdjustmentReason.Unknown
            };
            Input.Decisions.Add(input);
            Decisions.Add(new(location.Id, location.LocationCode, location.Status,
                attempt?.Entries.Where(item => item.Difference != 0).ToArray() ?? [], conflicts));
        }
    }

    private static SharedAssignmentApproval? ParseApproval(string value)
    {
        var parts = value.Split('|');
        return parts.Length == 2 && Guid.TryParse(parts[0], out var productId) && Guid.TryParse(parts[1], out var locationId)
            ? new(productId, locationId)
            : null;
    }

    public sealed record ReviewInput(Guid LocationId, string LocationCode, CycleCountLocationStatus Status,
        IReadOnlyList<CycleCountEntryItem> Entries, IReadOnlyList<SharedLocationConflict> SharingConflicts);
    public sealed class InputModel { public Guid OperationId { get; set; } public string Pin { get; set; } = string.Empty; public List<DecisionInput> Decisions { get; set; } = []; }
    public sealed class DecisionInput
    {
        public Guid LocationId { get; set; }
        public Guid OperationId { get; set; }
        public CycleCountReviewDecision Decision { get; set; }
        public CycleCountAdjustmentReason? Reason { get; set; }
        public string? Notes { get; set; }
        public List<string> SharedApprovals { get; set; } = [];
    }
}
