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

    public DashboardProductsDto Products { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken, int productPage = 1)
    {
        Snapshot = await GetSnapshotAsync(false, cancellationToken);
        Products = await GetProductsAsync(14, productPage, false, cancellationToken);
    }

    public async Task<IActionResult> OnGetActivityAsync(int days = 90, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid || days is not (7 or 14 or 90)) return BadRequest();
        NoHttpCache();
        var key = $"reporting:dashboard-activity:{days}";
        if (refresh) memoryCache.Remove(key);
        if (!memoryCache.TryGetValue<DashboardActivityDto>(key, out var activity) || activity is null)
        {
            activity = await dashboardService.GetActivityAsync(timeProvider.GetUtcNow(), days, cancellationToken);
            memoryCache.Set(key, activity, CacheDuration);
        }
        return new JsonResult(activity);
    }

    public async Task<IActionResult> OnGetProductsAsync(int days = 14, int pageNumber = 1, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid || days is not (7 or 14 or 90) || pageNumber < 1) return BadRequest();
        NoHttpCache();
        return new JsonResult(await GetProductsAsync(days, pageNumber, refresh, cancellationToken));
    }

    private async Task<DashboardProductsDto> GetProductsAsync(int days, int pageNumber, bool refresh, CancellationToken token)
    {
        pageNumber = Math.Max(1, pageNumber);
        var key = $"reporting:dashboard-products:{days}:{pageNumber}";
        if (refresh) memoryCache.Remove(key);
        if (memoryCache.TryGetValue<DashboardProductsDto>(key, out var products) && products is not null) return products;
        products = await dashboardService.GetActivityProductsAsync(timeProvider.GetUtcNow(), days, pageNumber, token);
        memoryCache.Set(key, products, CacheDuration);
        return products;
    }

    private void NoHttpCache()
    {
        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
    }

    public async Task<IActionResult> OnGetMetricsAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        NoHttpCache();
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
