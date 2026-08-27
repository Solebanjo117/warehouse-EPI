using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Web.Pages.Reports.Notifications;

public sealed class IndexModel(OperationalAlertService alerts, IMemoryCache cache) : PageModel
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public IActionResult OnGet() => User.IsInRole("ADMIN")
        ? RedirectToPage("/Admin/Inventory/Alerts")
        : RedirectToPage("/Reports/Inventory/Index", new { view = "exceptions" });

    public async Task<IActionResult> OnGetSnapshotAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Vary = "Cookie";
        var audience = User.IsInRole("ADMIN") ? OperationalAlertAudience.Admin : OperationalAlertAudience.Public;
        var key = $"reporting:operational-alerts:{audience}";
        if (refresh) cache.Remove(key);
        if (!cache.TryGetValue<OperationalAlertSnapshotDto>(key, out var snapshot) || snapshot is null)
        {
            snapshot = await alerts.GetSnapshotAsync(audience, cancellationToken);
            cache.Set(key, snapshot, CacheDuration);
        }
        return new JsonResult(snapshot);
    }
}
