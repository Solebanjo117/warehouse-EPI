using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Heatmap;

public sealed class IndexModel(
    HeatmapReportService heatmapService,
    ReportExportService exportService,
    WarehouseClock clock) : PageModel
{
    public HeatmapReportDto Report { get; private set; } = null!;
    public WarehouseMapView MapView { get; private set; } = null!;
    public IReadOnlyDictionary<Guid, RackHeatmapItemDto> RacksByElementId { get; private set; } = new Dictionary<Guid, RackHeatmapItemDto>();
    public IReadOnlyList<string> Rows { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? Metric { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Period { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? RowCode { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IActionResult OnGet()
    {
        var mapMetric = string.Equals(Metric, "access-frequency", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Metric, "activity", StringComparison.OrdinalIgnoreCase)
            ? "activity"
            : "occupancy";
        return RedirectToPage("/Locations/Index", new
        {
            viewMode = "map",
            mapMetric,
            period = Period,
            from = From,
            to = To,
            rowCode = RowCode,
            search = Search
        });
    }

    public async Task<IActionResult> OnGetExportAsync(
        string? format,
        CancellationToken cancellationToken)
    {
        if (!User.IsInRole("ADMIN"))
            return Forbid();

        var (filter, periodLabel) = await BuildFilterAsync(cancellationToken);
        var exportData = await heatmapService.GetHeatmapExportAsync(filter, cancellationToken);

        var dateStamp = exportData.GeneratedAtLocal.ToString("yyyy-MM-dd");
        var metricSlug = filter.Metric == HeatmapMetricType.AccessFrequency ? "Accesos" : "Ocupacion";

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = await exportService.ExportHeatmapToCsvAsync(
                exportData.Racks, exportData.Summary, filter, periodLabel, cancellationToken);
            return File(csv, "text/csv; charset=utf-8", $"Mapa_Calor_{metricSlug}_{dateStamp}.csv");
        }

        var excel = await exportService.ExportHeatmapToExcelAsync(
            exportData.Racks, exportData.Summary, filter, periodLabel, cancellationToken);
        return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Mapa_Calor_{metricSlug}_{dateStamp}.xlsx");
    }

    private async Task<(HeatmapReportFilter Filter, string PeriodLabel)> BuildFilterAsync(CancellationToken cancellationToken)
    {
        var metricType = string.Equals(Metric, "occupancy-density", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Metric, "occupancy", StringComparison.OrdinalIgnoreCase)
            ? HeatmapMetricType.OccupancyDensity
            : HeatmapMetricType.AccessFrequency;

        Metric = metricType == HeatmapMetricType.OccupancyDensity ? "occupancy-density" : "access-frequency";
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        RowCode = string.IsNullOrWhiteSpace(RowCode) ? null : RowCode.Trim();
        PageNumber = Math.Max(1, PageNumber);

        var today = await clock.GetDateAsync(DateTimeOffset.UtcNow, cancellationToken);
        DateOnly fromDate;
        DateOnly toDate;
        string periodLabel;

        if (metricType == HeatmapMetricType.OccupancyDensity)
        {
            fromDate = today;
            toDate = today;
            periodLabel = "Saldo actual en estantería";
        }
        else
        {
            var normalizedPeriod = Period?.Trim().ToLowerInvariant();
            if (normalizedPeriod == "7")
            {
                fromDate = today.AddDays(-6);
                toDate = today;
                periodLabel = "Últimos 7 días";
            }
            else if (normalizedPeriod == "14")
            {
                fromDate = today.AddDays(-13);
                toDate = today;
                periodLabel = "Últimos 14 días";
            }
            else if (normalizedPeriod == "custom" && From.HasValue && To.HasValue)
            {
                fromDate = From.Value <= To.Value ? From.Value : To.Value;
                toDate = From.Value <= To.Value ? To.Value : From.Value;
                periodLabel = $"{fromDate:dd/MM/yyyy} a {toDate:dd/MM/yyyy}";
            }
            else // default 30
            {
                Period = "30";
                fromDate = today.AddDays(-29);
                toDate = today;
                periodLabel = "Últimos 30 días";
            }
        }

        From = fromDate;
        To = toDate;

        var interval = await clock.GetUtcIntervalAsync(fromDate, toDate, cancellationToken);
        var filter = new HeatmapReportFilter(
            metricType,
            interval.FromInclusive,
            interval.ToExclusive,
            RowCode,
            Search,
            PageNumber,
            PageSize: 50);

        return (filter, periodLabel);
    }
}
