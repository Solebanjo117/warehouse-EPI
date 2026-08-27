using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Web.Pages.Admin.Inventory;

[Authorize(Policy = "AdminOnly")]
public sealed class AlertsModel(OperationalAlertService alerts) : PageModel
{
    private const int PageSize = 25;
    public string View { get; private set; } = "negative";
    public string? Search { get; private set; }
    public string? Attention { get; private set; }
    public OperationalAlertSnapshotDto Snapshot { get; private set; } = new(OperationalAlertAudience.Admin,
        DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, 0, 0, 0, []);
    public OperationalAlertPageDto AlertPage { get; private set; } = new([], 0, 1, PageSize);
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(AlertPage.TotalCount / (double)PageSize));

    public async Task OnGetAsync(string? view, string? search, string? attention, int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        View = NormalizeView(view);
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        Attention = string.IsNullOrWhiteSpace(attention) ? null : attention.Trim().ToLowerInvariant();
        Snapshot = await alerts.GetSnapshotAsync(OperationalAlertAudience.Admin, cancellationToken);
        AlertPage = await alerts.GetPageAsync(Category(View, Attention), Search, pageNumber, PageSize, cancellationToken);
    }

    public int Count(OperationalAlertCategory category) => Snapshot.Items.FirstOrDefault(x => x.Category == category)?.Count ?? 0;

    private static string NormalizeView(string? value) => value?.ToLowerInvariant() switch
    {
        "minimum" or "unassigned" or "restricted" or "stagnant" or "cycle" or "wip" => value.ToLowerInvariant(),
        _ => "negative"
    };

    private static OperationalAlertCategory Category(string view, string? attention) => view switch
    {
        "minimum" => OperationalAlertCategory.BelowMinimum,
        "unassigned" => OperationalAlertCategory.UnassignedBalance,
        "restricted" => OperationalAlertCategory.RestrictedInventory,
        "stagnant" => OperationalAlertCategory.StagnantInventory,
        "cycle" when attention == "stale" => OperationalAlertCategory.CycleCountStale,
        "cycle" => OperationalAlertCategory.CycleCountPending,
        "wip" => OperationalAlertCategory.AgedWip,
        _ => OperationalAlertCategory.NegativeInventory
    };
}
