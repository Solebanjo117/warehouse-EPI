using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Inventory;

public sealed class IndexModel(
    InventoryAnalyticsService analyticsService,
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
    public string Period { get; private set; } = "90";
    public string Status { get; private set; } = "active";
    public string? Search { get; private set; }
    public short? UnitId { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public LocationOccupancyReportDto Occupancy { get; private set; } = new(
        new(0, 0, 0, 0, 0, 0),
        []);
    public InventoryAnalyticsPage<SkuRotationMetricDto> Rotation { get; private set; } = new([], 0, 1, PageSize);
    public InventoryAnalyticsPage<StagnantProductDto> Stagnant { get; private set; } = new([], 0, 1, PageSize);
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];

    public async Task OnGetAsync(
        string? view,
        string? period,
        string? status,
        string? search,
        short? unitId,
        int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        Normalize(view, period, status, search, unitId);
        var nowUtc = timeProvider.GetUtcNow();
        UpdatedAt = await clock.ConvertAsync(nowUtc, cancellationToken);
        await LoadDisplayTimeZoneAsync(cancellationToken);

        if (View == "occupancy")
        {
            Occupancy = await GetCachedAsync(
                "reporting:inventory-analytics:occupancy",
                () => analyticsService.GetOccupancyAsync(cancellationToken));
            return;
        }

        await LoadUnitOptionsAsync(cancellationToken);
        var filter = await BuildFilterAsync(pageNumber, nowUtc, cancellationToken);
        var cacheKey = CacheKey(View, filter, Period);
        if (View == "rotation")
        {
            Rotation = await GetCachedAsync(
                cacheKey,
                () => analyticsService.GetRotationPageAsync(filter, cancellationToken));
        }
        else
        {
            Stagnant = await GetCachedAsync(
                cacheKey,
                () => analyticsService.GetStagnantPageAsync(filter, nowUtc, cancellationToken));
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
        Normalize(view, period, status, search, unitId);
        if (View == "occupancy")
            return BadRequest("La ocupación no se exporta en la fase 13.4.");
        if (format is not ("csv" or "xlsx"))
            return BadRequest("El formato debe ser csv o xlsx.");

        var nowUtc = timeProvider.GetUtcNow();
        var filter = await BuildFilterAsync(1, nowUtc, cancellationToken);
        var localNow = await clock.ConvertAsync(nowUtc, cancellationToken);
        byte[] bytes;
        string fileName;
        if (View == "rotation")
        {
            var batch = await analyticsService.GetRotationExportAsync(filter, 10000, cancellationToken);
            if (batch.ExceedsLimit)
                return ExportLimit(batch.TotalRows, batch.MaximumRows);
            bytes = format == "xlsx"
                ? await exportService.ExportRotationToExcelAsync(batch.Items, filter, cancellationToken)
                : await exportService.ExportRotationToCsvAsync(batch.Items, filter, cancellationToken);
            fileName = $"rotacion-inventario-{localNow:yyyyMMdd-HHmmss}.{format}";
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
        if (View == "rotation" && Period != "all")
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
            PageSize);
    }

    private void Normalize(string? view, string? period, string? status, string? search, short? unitId)
    {
        View = view is "rotation" or "stagnant" ? view : "occupancy";
        Period = period is "30" or "180" or "all" ? period : "90";
        Status = status is "inactive" or "all" ? status : "active";
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        UnitId = unitId;
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

    private async Task<T> GetCachedAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        if (memoryCache.TryGetValue<T>(key, out var cached) && cached is not null)
            return cached;
        var value = await factory();
        memoryCache.Set(key, value, CacheDuration);
        return value;
    }

    private static string CacheKey(string view, InventoryAnalyticsFilter filter, string period) =>
        $"reporting:inventory-analytics:{view}:{period}:{filter.ProductStatus}:{filter.Search}:{filter.UnitId}:{filter.PageNumber}";

    private BadRequestObjectResult ExportLimit(int totalRows, int maximumRows) => BadRequest(
        $"La exportación contiene {totalRows:N0} productos y supera el límite de {maximumRows:N0}. Aplica filtros más específicos.");
}
