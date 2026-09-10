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
    public string? StagnantCategory => StagnantCategoryFilter == WarehouseEPI.Infrastructure.Reporting.StagnantCategory.Days90Plus ? "90plus" : null;
    public string? Search { get; private set; }
    public short? UnitId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string TimeZoneId { get; private set; } = "UTC";
    public LocationOccupancyReportDto Occupancy { get; private set; } = new(new(0, 0, 0, 0, 0, 0), []);
    public InventoryAnalyticsPage<SkuExitActivityMetricDto> Activity { get; private set; } = new([], 0, 1, PageSize);
    public InventoryAnalyticsPage<StagnantProductDto> Stagnant { get; private set; } = new([], 0, 1, PageSize);
    public LotAgingReportDto LotAging { get; private set; } = new(new(0, 0, 0, 0, 0), new([], 0, 1, PageSize));
    public SkuCoverageReportDto Coverage { get; private set; } = new(new(0, 0, 0, 0, 0, 0, 0), new([], 0, 1, PageSize));
    public LotAgeBucket? AgeBucket { get; private set; }
    public CoverageClassification? CoverageClass { get; private set; }
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
        string? ageBucket = null,
        string? coverageClass = null,
        int pageNumber = 1,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        Normalize(view, exception, period, status, search, unitId, stagnantCategory, ageBucket, coverageClass);
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

        if (View == "lot-aging")
        {
            var cacheKey = $"reporting:inventory-analytics:lot-aging:{filter.ProductStatus}:{filter.Search}:{filter.UnitId}:{filter.AgeBucket}:{filter.PageNumber}";
            var cached = await GetCachedAsync(
                cacheKey,
                () => analyticsService.GetLotAgingPageAsync(filter, nowUtc, cancellationToken),
                refresh);
            LotAging = cached.Data;
            UpdatedAt = TimeZoneInfo.ConvertTime(cached.GeneratedAtUtc, displayTimeZone);
            return;
        }

        if (View == "coverage")
        {
            var cacheKey = $"reporting:inventory-analytics:coverage:{Period}:{filter.ProductStatus}:{filter.Search}:{filter.UnitId}:{filter.CoverageClassification}:{filter.PageNumber}";
            var cached = await GetCachedAsync(
                cacheKey,
                () => analyticsService.GetCoveragePageAsync(filter, nowUtc, cancellationToken),
                refresh);
            Coverage = cached.Data;
            UpdatedAt = TimeZoneInfo.ConvertTime(cached.GeneratedAtUtc, displayTimeZone);
            return;
        }

        var commonCacheKey = CacheKey(View, filter, Period);
        if (View == "activity")
        {
            var cached = await GetCachedAsync(
                commonCacheKey,
                () => analyticsService.GetExitActivityPageAsync(filter, cancellationToken),
                refresh);
            Activity = cached.Data;
            UpdatedAt = TimeZoneInfo.ConvertTime(cached.GeneratedAtUtc, displayTimeZone);
        }
        else
        {
            var cached = await GetCachedAsync(
                commonCacheKey,
                () => analyticsService.GetStagnantPageAsync(filter, nowUtc, cancellationToken),
                refresh);
            Stagnant = cached.Data;
            UpdatedAt = TimeZoneInfo.ConvertTime(cached.GeneratedAtUtc, displayTimeZone);
        }
    }

    public async Task<IActionResult> OnGetExportAsync(
        string view,
        string? exception,
        string format,
        string? period,
        string? status,
        string? search,
        short? unitId,
        string? stagnantCategory = null,
        string? ageBucket = null,
        string? coverageClass = null,
        CancellationToken cancellationToken = default)
    {
        if (!User.IsInRole("ADMIN"))
            return Forbid();

        Normalize(view, exception, period, status, search, unitId, stagnantCategory, ageBucket, coverageClass);
        if (format is not ("csv" or "xlsx"))
            return BadRequest("El formato debe ser csv o xlsx.");

        var nowUtc = timeProvider.GetUtcNow();
        var localNow = await clock.ConvertAsync(nowUtc, cancellationToken);
        byte[] bytes;
        string fileName;

        if (View == "occupancy")
        {
            var cached = await GetCachedAsync(
                "reporting:inventory-analytics:occupancy",
                () => analyticsService.GetOccupancyAsync(cancellationToken),
                false);
            bytes = format == "xlsx"
                ? await exportService.ExportOccupancyToExcelAsync(cached.Data, cached.GeneratedAtUtc, cancellationToken)
                : await exportService.ExportOccupancyToCsvAsync(cached.Data, cached.GeneratedAtUtc, cancellationToken);
            fileName = $"ocupacion-almacen-{localNow:yyyyMMdd-HHmmss}.{format}";
        }
        else if (View == "exceptions")
        {
            if (ExceptionView == "minimum")
            {
                var batch = await inventoryQueryService.GetBelowMinimumAlertExportAsync(Search, 10000, cancellationToken);
                if (batch.ExceedsLimit)
                    return ExportLimit(batch.TotalRows, batch.MaximumRows, "productos");
                bytes = format == "xlsx"
                    ? await exportService.ExportMinimumExceptionsToExcelAsync(batch.Items, Search, cancellationToken)
                    : await exportService.ExportMinimumExceptionsToCsvAsync(batch.Items, Search, cancellationToken);
                fileName = $"productos-bajo-minimo-{localNow:yyyyMMdd-HHmmss}.{format}";
            }
            else
            {
                var batch = await inventoryQueryService.GetNegativeAlertExportAsync(Search, 10000, cancellationToken);
                if (batch.ExceedsLimit)
                    return ExportLimit(batch.TotalRows, batch.MaximumRows, "posiciones producto-ubicación");
                bytes = format == "xlsx"
                    ? await exportService.ExportNegativeExceptionsToExcelAsync(batch.Items, Search, cancellationToken)
                    : await exportService.ExportNegativeExceptionsToCsvAsync(batch.Items, Search, cancellationToken);
                fileName = $"saldos-negativos-{localNow:yyyyMMdd-HHmmss}.{format}";
            }
        }
        else if (View == "lot-aging")
        {
            var filter = await BuildFilterAsync(1, nowUtc, cancellationToken);
            var batch = await analyticsService.GetLotAgingExportAsync(filter, nowUtc, 10000, cancellationToken);
            if (batch.ExceedsLimit)
                return ExportLimit(batch.TotalRows, batch.MaximumRows, "lotes");
            var agingReport = await analyticsService.GetLotAgingPageAsync(filter, nowUtc, cancellationToken);
            bytes = format == "xlsx"
                ? await exportService.ExportLotAgingToExcelAsync(batch.Items, agingReport.Summary, filter, cancellationToken)
                : await exportService.ExportLotAgingToCsvAsync(batch.Items, filter, cancellationToken);
            fileName = $"antiguedad-lotes-{localNow:yyyyMMdd-HHmmss}.{format}";
        }
        else if (View == "coverage")
        {
            var filter = await BuildFilterAsync(1, nowUtc, cancellationToken);
            var batch = await analyticsService.GetCoverageExportAsync(filter, nowUtc, 10000, cancellationToken);
            if (batch.ExceedsLimit)
                return ExportLimit(batch.TotalRows, batch.MaximumRows, "productos");
            var coverageReport = await analyticsService.GetCoveragePageAsync(filter, nowUtc, cancellationToken);
            bytes = format == "xlsx"
                ? await exportService.ExportCoverageToExcelAsync(batch.Items, coverageReport.Summary, filter, cancellationToken)
                : await exportService.ExportCoverageToCsvAsync(batch.Items, filter, cancellationToken);
            fileName = $"cobertura-consumo-{localNow:yyyyMMdd-HHmmss}.{format}";
        }
        else
        {
            var filter = await BuildFilterAsync(1, nowUtc, cancellationToken);
            if (View == "activity")
            {
                var batch = await analyticsService.GetExitActivityExportAsync(filter, 10000, cancellationToken);
                if (batch.ExceedsLimit)
                    return ExportLimit(batch.TotalRows, batch.MaximumRows, "productos");
                bytes = format == "xlsx"
                    ? await exportService.ExportExitActivityToExcelAsync(batch.Items, filter, cancellationToken)
                    : await exportService.ExportExitActivityToCsvAsync(batch.Items, filter, cancellationToken);
                fileName = $"actividad-salidas-sku-{localNow:yyyyMMdd-HHmmss}.{format}";
            }
            else
            {
                var batch = await analyticsService.GetStagnantExportAsync(filter, nowUtc, 10000, cancellationToken);
                if (batch.ExceedsLimit)
                    return ExportLimit(batch.TotalRows, batch.MaximumRows, "productos");
                bytes = format == "xlsx"
                    ? await exportService.ExportStagnantToExcelAsync(batch.Items, filter, cancellationToken)
                    : await exportService.ExportStagnantToCsvAsync(batch.Items, filter, cancellationToken);
                fileName = $"productos-estancados-{localNow:yyyyMMdd-HHmmss}.{format}";
            }
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

    public static string FormatCategory(WarehouseEPI.Infrastructure.Reporting.StagnantCategory category) => category switch
    {
        WarehouseEPI.Infrastructure.Reporting.StagnantCategory.Days30To59 => "30–59 días",
        WarehouseEPI.Infrastructure.Reporting.StagnantCategory.Days60To89 => "60–89 días",
        WarehouseEPI.Infrastructure.Reporting.StagnantCategory.Days90Plus => "90+ días",
        WarehouseEPI.Infrastructure.Reporting.StagnantCategory.NeverExited => "Nunca salió",
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
            if (Period == "this-month")
            {
                from = new DateOnly(today.Year, today.Month, 1);
                to = today;
            }
            else if (Period == "last-month")
            {
                var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
                var lastDayOfLastMonth = firstOfThisMonth.AddDays(-1);
                from = new DateOnly(lastDayOfLastMonth.Year, lastDayOfLastMonth.Month, 1);
                to = lastDayOfLastMonth;
            }
            else
            {
                var days = Period switch { "30" => 30, "180" => 180, _ => 90 };
                from = today.AddDays(-(days - 1));
                to = today;
            }
        }
        else if (View == "coverage")
        {
            var today = await clock.GetDateAsync(nowUtc, cancellationToken);
            var days = Period switch { "60" => 60, "90" => 90, _ => 30 };
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
            StagnantCategoryFilter,
            AgeBucket,
            CoverageClass);
    }

    private void Normalize(
        string? view,
        string? exception,
        string? period,
        string? status,
        string? search,
        short? unitId,
        string? stagnantCategory = null,
        string? ageBucket = null,
        string? coverageClass = null)
    {
        View = view switch
        {
            "rotation" => "activity",
            "activity" or "stagnant" or "exceptions" or "lot-aging" or "coverage" => view,
            _ => "occupancy"
        };
        ExceptionView = exception == "minimum" ? "minimum" : "negative";
        Period = View == "coverage"
            ? (period is "60" or "90" ? period : "30")
            : (period is "30" or "180" or "all" or "this-month" or "last-month" ? period : "90");
        Status = status is "inactive" or "all" ? status : "active";
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        UnitId = unitId;
        StagnantCategoryFilter = string.Equals(stagnantCategory, "90plus", StringComparison.OrdinalIgnoreCase)
            ? WarehouseEPI.Infrastructure.Reporting.StagnantCategory.Days90Plus
            : null;
        AgeBucket = Enum.TryParse<LotAgeBucket>(ageBucket, true, out var parsedBucket) && parsedBucket != LotAgeBucket.All
            ? parsedBucket
            : null;
        CoverageClass = Enum.TryParse<CoverageClassification>(coverageClass, true, out var parsedClass) && parsedClass != CoverageClassification.All
            ? parsedClass
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
        TimeZoneId = settings.TimeZoneId;
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
        $"reporting:inventory-analytics:{view}:{period}:{filter.ProductStatus}:{filter.Search}:{filter.UnitId}:{filter.StagnantCategory}:{filter.AgeBucket}:{filter.CoverageClassification}:{filter.PageNumber}";

    private BadRequestObjectResult ExportLimit(int totalRows, int maximumRows, string population) => BadRequest(
        $"La exportación contiene {totalRows:N0} {population} y supera el límite de {maximumRows:N0}. Aplica filtros más específicos.");

    private sealed record CachedAnalyticsResult<T>(T Data, DateTimeOffset GeneratedAtUtc);
    private sealed record ExceptionReportData(
        InventoryAlertSummary Summary,
        InventoryAlertPage<NegativeInventoryAlert>? Negative,
        InventoryAlertPage<MinimumStockInventoryAlert>? Minimum);
}
