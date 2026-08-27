using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Inventory;

public sealed class IndexModel(
    InventoryAnalyticsService analyticsService,
    InventoryQueryService inventoryQueryService,
    ReportExportService exportService,
    WarehouseDbContext dbContext,
    WarehouseClock clock,
    WarehouseSettingsService settingsService,
    IMemoryCache memoryCache,
    TimeProvider timeProvider) : PageModel
{
    private const int PageSize = 25;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
    private TimeZoneInfo displayTimeZone = TimeZoneInfo.Utc;

    public string View { get; private set; } = "occupancy";
    public string ExceptionView { get; private set; } = "negative";
    public string Period { get; private set; } = "90";
    public string Status { get; private set; } = "active";
    public StagnantCategory? StagnantCategoryFilter { get; private set; }
    public string? Search { get; private set; }
    public short? UnitId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public LocationOccupancyReportDto Occupancy { get; private set; } = new(new(0, 0, 0, 0, 0, 0), []);
    public InventoryAnalyticsPage<SkuExitActivityMetricDto> Activity { get; private set; } = new([], 0, 1, PageSize);
    public InventoryAnalyticsPage<StagnantProductDto> Stagnant { get; private set; } = new([], 0, 1, PageSize);
    public InventoryAlertSummary ExceptionSummary { get; private set; } = new(0, 0, 0);
    public InventoryAlertPage<NegativeInventoryAlert> NegativeExceptions { get; private set; } = new([], 0);
    public InventoryAlertPage<MinimumStockInventoryAlert> MinimumExceptions { get; private set; } = new([], 0);
    public int ExceptionPageNumber { get; private set; } = 1;
    public int ExceptionTotalPages { get; private set; } = 1;
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    public async Task OnGetAsync(
        string? view,
        string? exception,
        string? period,
        string? status,
        string? search,
        short? unitId,
        string? stagnantCategory = null,
        int pageNumber = 1,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        Normalize(view, exception, period, status, search, unitId, stagnantCategory);
        await LoadDisplayTimeZoneAsync(cancellationToken);

        if (View == "occupancy")
        {
            var cached = await GetCachedAsync(
                "reporting:inventory-analytics:occupancy",
                () => analyticsService.GetOccupancyAsync(cancellationToken),
                refresh);
            Occupancy = cached.Data;
            UpdatedAt = TimeZoneInfo.ConvertTime(cached.GeneratedAtUtc, displayTimeZone);
            return;
        }

        if (View == "exceptions")
        {
            var requestedPage = Math.Max(1, pageNumber);
            var key = $"reporting:inventory-analytics:exceptions:{ExceptionView}:{Search}:{requestedPage}";
            var cached = await GetCachedAsync(
                key,
                async () =>
                {
                    var summary = await inventoryQueryService.GetAlertSummaryAsync(cancellationToken);
                    if (ExceptionView == "minimum")
                    {
                        var minimum = await inventoryQueryService.GetBelowMinimumAlertPageAsync(
                            Search, requestedPage, PageSize, cancellationToken);
                        return new ExceptionReportData(summary, null, minimum);
                    }

                    var negative = await inventoryQueryService.GetNegativeAlertPageAsync(
                        Search, requestedPage, PageSize, cancellationToken);
                    return new ExceptionReportData(summary, negative, null);
                },
                refresh);
            ExceptionSummary = cached.Data.Summary;
            NegativeExceptions = cached.Data.Negative ?? NegativeExceptions;
            MinimumExceptions = cached.Data.Minimum ?? MinimumExceptions;
            var totalCount = ExceptionView == "minimum"
                ? MinimumExceptions.TotalCount
                : NegativeExceptions.TotalCount;
            ExceptionTotalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
            ExceptionPageNumber = Math.Clamp(requestedPage, 1, ExceptionTotalPages);
            UpdatedAt = TimeZoneInfo.ConvertTime(cached.GeneratedAtUtc, displayTimeZone);
            return;
        }

        await LoadUnitOptionsAsync(cancellationToken);
        var nowUtc = timeProvider.GetUtcNow();
        var filter = await BuildFilterAsync(pageNumber, nowUtc, cancellationToken);
        var cacheKey = CacheKey(View, filter, Period);
        if (View == "activity")
        {
            var cached = await GetCachedAsync(
                cacheKey,
                () => analyticsService.GetExitActivityPageAsync(filter, cancellationToken),
                refresh);
            Activity = cached.Data;
            UpdatedAt = TimeZoneInfo.ConvertTime(cached.GeneratedAtUtc, displayTimeZone);
        }
        else
        {
            var cached = await GetCachedAsync(
                cacheKey,
                () => analyticsService.GetStagnantPageAsync(filter, nowUtc, cancellationToken),
                refresh);
            Stagnant = cached.Data;
            UpdatedAt = TimeZoneInfo.ConvertTime(cached.GeneratedAtUtc, displayTimeZone);
        }
    }

    public async Task<IActionResult> OnGetExportAsync(
        string view,
        string format,
        string? period,
        string? status,
        string? search,
        short? unitId,
        CancellationToken cancellationToken = default)
    {
        if (!User.IsInRole("ADMIN"))
            return Forbid();

        Normalize(view, null, period, status, search, unitId);
        if (View is "occupancy" or "exceptions")
            return BadRequest("La ocupación y las excepciones no se exportan en esta fase.");
        if (format is not ("csv" or "xlsx"))
            return BadRequest("El formato debe ser csv o xlsx.");

        var nowUtc = timeProvider.GetUtcNow();
        var filter = await BuildFilterAsync(1, nowUtc, cancellationToken);
        var localNow = await clock.ConvertAsync(nowUtc, cancellationToken);
        byte[] bytes;
        string fileName;
        if (View == "activity")
        {
            var batch = await analyticsService.GetExitActivityExportAsync(filter, 10000, cancellationToken);
            if (batch.ExceedsLimit)
                return ExportLimit(batch.TotalRows, batch.MaximumRows);
            bytes = format == "xlsx"
                ? await exportService.ExportExitActivityToExcelAsync(batch.Items, filter, cancellationToken)
                : await exportService.ExportExitActivityToCsvAsync(batch.Items, filter, cancellationToken);
            fileName = $"actividad-salidas-sku-{localNow:yyyyMMdd-HHmmss}.{format}";
        }
        else
        {
            var batch = await analyticsService.GetStagnantExportAsync(filter, nowUtc, 10000, cancellationToken);
            if (batch.ExceedsLimit)
                return ExportLimit(batch.TotalRows, batch.MaximumRows);
            bytes = format == "xlsx"
                ? await exportService.ExportStagnantToExcelAsync(batch.Items, filter, cancellationToken)
                : await exportService.ExportStagnantToCsvAsync(batch.Items, filter, cancellationToken);
            fileName = $"productos-estancados-{localNow:yyyyMMdd-HHmmss}.{format}";
        }

        return File(
            bytes,
            format == "xlsx"
                ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                : "text/csv; charset=utf-8",
            fileName);
    }

    public string FormatLocalDate(DateTimeOffset? value) => value is null
        ? "Nunca"
        : TimeZoneInfo.ConvertTime(value.Value, displayTimeZone).ToString("dd/MM/yyyy HH:mm");

    public static string FormatCategory(StagnantCategory category) => category switch
    {
        StagnantCategory.Days30To59 => "30–59 días",
        StagnantCategory.Days60To89 => "60–89 días",
        StagnantCategory.Days90Plus => "90+ días",
        StagnantCategory.NeverExited => "Nunca salió",
        _ => category.ToString()
    };

    private async Task<InventoryAnalyticsFilter> BuildFilterAsync(
        int pageNumber,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        DateOnly? from = null;
        DateOnly? to = null;
        if (View == "activity" && Period != "all")
        {
            var today = await clock.GetDateAsync(nowUtc, cancellationToken);
            var days = Period switch { "30" => 30, "180" => 180, _ => 90 };
            from = today.AddDays(-(days - 1));
            to = today;
        }
        var interval = await clock.GetUtcIntervalAsync(from, to, cancellationToken);
        return new(
            interval.FromInclusive,
            interval.ToExclusive,
            Status,
            Search,
            UnitId,
            Math.Max(1, pageNumber),
            PageSize,
            StagnantCategoryFilter);
    }

    private void Normalize(
        string? view,
        string? exception,
        string? period,
        string? status,
        string? search,
        short? unitId,
        string? stagnantCategory = null)
    {
        View = view switch
        {
            "rotation" => "activity",
            "activity" or "stagnant" or "exceptions" => view,
            _ => "occupancy"
        };
        ExceptionView = exception == "minimum" ? "minimum" : "negative";
        Period = period is "30" or "180" or "all" ? period : "90";
        Status = status is "inactive" or "all" ? status : "active";
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        UnitId = unitId;
        StagnantCategoryFilter = string.Equals(stagnantCategory, "90plus", StringComparison.OrdinalIgnoreCase)
            ? WarehouseEPI.Infrastructure.Reporting.StagnantCategory.Days90Plus
            : null;
    }

    private async Task LoadUnitOptionsAsync(CancellationToken cancellationToken)
    {
        UnitOptions = await dbContext.Units
            .AsNoTracking()
            .OrderBy(unit => unit.Code)
            .Select(unit => new SelectListItem($"{unit.Code} — {unit.Name}", unit.Id.ToString()))
            .ToListAsync(cancellationToken);
    }

    private async Task LoadDisplayTimeZoneAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        displayTimeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
    }

    private async Task<CachedAnalyticsResult<T>> GetCachedAsync<T>(
        string key,
        Func<Task<T>> factory,
        bool refresh) where T : class
    {
        if (refresh)
            memoryCache.Remove(key);
        if (memoryCache.TryGetValue<CachedAnalyticsResult<T>>(key, out var cached) && cached is not null)
            return cached;

        var value = new CachedAnalyticsResult<T>(await factory(), timeProvider.GetUtcNow());
        memoryCache.Set(key, value, CacheDuration);
        return value;
    }

    private static string CacheKey(string view, InventoryAnalyticsFilter filter, string period) =>
        $"reporting:inventory-analytics:{view}:{period}:{filter.ProductStatus}:{filter.Search}:{filter.UnitId}:{filter.StagnantCategory}:{filter.PageNumber}";

    private BadRequestObjectResult ExportLimit(int totalRows, int maximumRows) => BadRequest(
        $"La exportación contiene {totalRows:N0} productos y supera el límite de {maximumRows:N0}. Aplica filtros más específicos.");

    private sealed record CachedAnalyticsResult<T>(T Data, DateTimeOffset GeneratedAtUtc);
    private sealed record ExceptionReportData(
        InventoryAlertSummary Summary,
        InventoryAlertPage<NegativeInventoryAlert>? Negative,
        InventoryAlertPage<MinimumStockInventoryAlert>? Minimum);
}
