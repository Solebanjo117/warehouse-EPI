using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class CalendarModel(CycleCountService cycleCounts, WarehouseClock clock, TimeProvider timeProvider, IStringLocalizer<OperationsTexts> texts) : PageModel
{
    public PagedResult<CycleCountCalendarItem> Result { get; private set; } = new([], 0, 1, 25);
    public IReadOnlyList<CycleCountCalendarItem> Plans => Result.Items;
    public DateOnly Today { get; private set; }
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public string SelectedMonth { get; private set; } = string.Empty;
    public string ViewMode { get; private set; } = "month";
    public bool IsOverdue => ViewMode == "overdue";
    [BindProperty] public List<Guid> PlanIds { get; set; } = [];
    [BindProperty] public string Pin { get; set; } = string.Empty;
    [BindProperty] public Guid OperationId { get; set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(string? month, string? view, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        await LoadAsync(month, view, pageNumber, cancellationToken);
        OperationId = Guid.NewGuid();
    }

    public async Task<IActionResult> OnPostReleaseAsync(string? month, string? view, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        var result = await cycleCounts.ReleaseScheduledAsync(new(Pin, PlanIds, OperationId == Guid.Empty ? Guid.NewGuid() : OperationId), cancellationToken);
        Pin = string.Empty;
        if (result.Status == CycleCountStatus.Success && result.CampaignId is Guid campaignId)
            return RedirectToPage("Details", new { id = campaignId });
        await LoadAsync(month, view, pageNumber, cancellationToken);
        Error = CycleCountPresentation.StatusMessage(result, texts);
        return Page();
    }

    private async Task LoadAsync(string? month, string? view, int pageNumber, CancellationToken cancellationToken)
    {
        Today = await clock.GetDateAsync(timeProvider.GetUtcNow(), cancellationToken);
        ViewMode = string.Equals(view, "overdue", StringComparison.OrdinalIgnoreCase) ? "overdue" : "month";
        var selected = new DateOnly(Today.Year, Today.Month, 1);
        if (!string.IsNullOrWhiteSpace(month) &&
            !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out selected))
        {
            Error = texts["El mes indicado no es válido. Selecciona un mes con el formato año-mes."];
            selected = new DateOnly(Today.Year, Today.Month, 1);
        }
        From = new DateOnly(selected.Year, selected.Month, 1);
        To = From.AddMonths(1).AddDays(-1);
        SelectedMonth = From.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        Result = await cycleCounts.GetCalendarAsync(new(From, To, Today, IsOverdue, pageNumber, 25), cancellationToken);
    }
}
