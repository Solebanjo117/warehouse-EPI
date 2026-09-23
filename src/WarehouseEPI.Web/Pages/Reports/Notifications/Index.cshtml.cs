using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Reports.Notifications;

public sealed class IndexModel(
    OperationalAlertService alerts,
    IMemoryCache cache,
    IStringLocalizer<CatalogTexts> texts) : PageModel
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
        return new JsonResult(LocalizeSnapshot(snapshot, texts));
    }

    internal static OperationalAlertSnapshotDto LocalizeSnapshot(
        OperationalAlertSnapshotDto snapshot,
        IStringLocalizer<CatalogTexts> texts) => snapshot with
        {
            Items = snapshot.Items.Select(item => item with
            {
                Title = texts[item.Title],
                Description = LocalizeDescription(item, texts)
            }).ToArray()
        };

    private static string LocalizeDescription(
        OperationalAlertItemDto item,
        IStringLocalizer<CatalogTexts> texts)
    {
        if (item.Category != OperationalAlertCategory.AgedWip)
            return texts[item.Description];

        var days = System.Text.RegularExpressions.Regex.Match(item.Description, @"\d+").Value;
        return texts["Posiciones WIP positivas con lote de {0} días o más.", days];
    }
}
