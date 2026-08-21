using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Infrastructure.Reporting;

namespace WarehouseEPI.Web.Pages.Reports.Dashboard;

public sealed class IndexModel(
    DailyDashboardService dashboardService,
    IMemoryCache memoryCache,
    TimeProvider timeProvider) : PageModel
{
    private const string CacheKey = "reporting:daily-dashboard:14-days";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public DailyDashboardSnapshotDto Snapshot { get; private set; } = new(
        DateOnly.MinValue,
        DateTimeOffset.MinValue,
        new DailyDashboardMetricsDto(0, 0, 0, 0, []));

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Snapshot = await GetSnapshotAsync(false, cancellationToken);

    public async Task<IActionResult> OnGetMetricsAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
        return new JsonResult(await GetSnapshotAsync(refresh, cancellationToken));
    }

    private async Task<DailyDashboardSnapshotDto> GetSnapshotAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (refresh)
            memoryCache.Remove(CacheKey);
        if (memoryCache.TryGetValue<DailyDashboardSnapshotDto>(CacheKey, out var cached) && cached is not null)
            return cached;

        var snapshot = await dashboardService.GetSnapshotAsync(
            timeProvider.GetUtcNow(),
            trendDays: 14,
            cancellationToken);
        memoryCache.Set(CacheKey, snapshot, CacheDuration);
        return snapshot;
    }
}
