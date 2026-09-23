using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Locations;

namespace WarehouseEPI.Web.Pages.Reports.Heatmap;

public sealed class IndexModel(
    HeatmapReportService heatmapService,
    ReportExportService exportService,
    WarehouseClock clock) : PageModel
{
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

    public IActionResult OnGet()
    {
        var mapMetric = HeatmapQueryNormalizer.NormalizeLegacyMetric(Metric, "occupancy");
        var destination = User.IsInRole("ADMIN")
            ? "/Admin/Catalogs/Locations/Index"
            : "/Locations/Index";
        return RedirectToPage(destination, new
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

        var mapMetric = HeatmapQueryNormalizer.NormalizeLegacyMetric(Metric, "activity");
        var heatmapQuery = await HeatmapQueryNormalizer.BuildAsync(
            mapMetric, Period, From, To, clock, RowCode, Search, cancellationToken);
        var exportData = await heatmapService.GetHeatmapExportAsync(heatmapQuery.Filter, cancellationToken);

        var dateStamp = exportData.GeneratedAtLocal.ToString("yyyy-MM-dd");
        var metricSlug = heatmapQuery.Filter.Metric == HeatmapMetricType.AccessFrequency ? "Accesos" : "Ocupacion";

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = await exportService.ExportHeatmapToCsvAsync(
                exportData.Racks, exportData.Summary, heatmapQuery.Filter, heatmapQuery.PeriodLabel, cancellationToken);
            return File(csv, "text/csv; charset=utf-8", $"Mapa_Calor_{metricSlug}_{dateStamp}.csv");
        }

        var excel = await exportService.ExportHeatmapToExcelAsync(
            exportData.Racks, exportData.Summary, heatmapQuery.Filter, heatmapQuery.PeriodLabel, cancellationToken);
        return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Mapa_Calor_{metricSlug}_{dateStamp}.xlsx");
    }
}
