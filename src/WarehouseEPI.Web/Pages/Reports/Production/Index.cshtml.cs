using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Production;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(
    ProductionReportService reports,
    WarehouseClock clock,
    WarehouseDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "orders";
    [BindProperty(SupportsGet = true)] public string Period { get; set; } = "30";
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ProductId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? StageId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ShiftId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public bool AlertsOnly { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 25;
    [BindProperty(SupportsGet = true)] public bool Print { get; set; }

    public ProductionReportPage Report { get; private set; } = null!;
    public IReadOnlyList<SelectListItem> Products { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Stages { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Shifts { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken token)
    {
        Normalize();
        await LoadOptionsAsync(token);
        var (filter, label) = await BuildFilterAsync(PageSize, token);
        Report = await reports.GetAsync(View, filter, label, token);
    }

    public async Task<IActionResult> OnGetExportAsync(string? format, CancellationToken token)
    {
        Normalize();
        var (filter, label) = await BuildFilterAsync(ProductionReportExportService.RowLimit, token);
        var report = await reports.GetAsync(View, filter with { Page = 1 }, label, token);
        try
        {
            var description = BuildFilterDescription(label);
            var stamp = report.GeneratedAtLocal.ToString("yyyy-MM-dd");
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                return File(ProductionReportExportService.ToCsv(View, report, description), "text/csv; charset=utf-8", $"Produccion_{View}_{stamp}.csv");
            return File(ProductionReportExportService.ToExcel(View, report, description),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Produccion_{View}_{stamp}.xlsx");
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    private async Task<(ProductionReportFilter Filter, string Label)> BuildFilterAsync(int pageSize, CancellationToken token)
    {
        var today = await clock.GetDateAsync(DateTimeOffset.UtcNow, token);
        DateOnly from;
        DateOnly to;
        string label;
        switch (Period)
        {
            case "today": from = to = today; label = "Hoy"; break;
            case "week":
                from = today.AddDays(-((7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7));
                to = today; label = "Esta semana"; break;
            case "month": from = new(today.Year, today.Month, 1); to = today; label = "Este mes"; break;
            case "custom" when From.HasValue && To.HasValue:
                from = From.Value; to = To.Value; label = $"{from:dd/MM/yyyy} a {to:dd/MM/yyyy}"; break;
            default: Period = "30"; from = today.AddDays(-29); to = today; label = "Últimos 30 días"; break;
        }
        if (to < from) (from, to) = (to, from);
        From = from; To = to;
        var interval = await clock.GetUtcIntervalAsync(from, to, token);
        return (new(interval.FromInclusive, interval.ToExclusive, Search, ProductId, StageId, ShiftId,
            ParseStatus(Status), AlertsOnly, Math.Max(1, PageNumber), pageSize), label);
    }

    private async Task LoadOptionsAsync(CancellationToken token)
    {
        Products = await db.ProductionWorkOrders.AsNoTracking().Select(x => x.Product).Distinct()
            .OrderBy(x => x.Sku).Select(x => new SelectListItem($"{x.Sku} · {x.Description}", x.Id.ToString())).ToListAsync(token);
        Stages = await db.ProductionStages.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync(token);
        Shifts = await db.ProductionShifts.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync(token);
    }

    private void Normalize()
    {
        View = View is "materials" or "rework" or "records" ? View : "orders";
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        PageNumber = Math.Max(1, PageNumber);
        PageSize = PageSize is 10 or 25 or 50 or 100 ? PageSize : 25;
    }

    private string BuildFilterDescription(string label) =>
        $"{label}; búsqueda={Search ?? "todas"}; producto={ProductId?.ToString() ?? "todos"}; proceso={StageId?.ToString() ?? "todos"}; estado={Status ?? "todos"}; alertas={(AlertsOnly ? "sí" : "no")}";

    private static ProductionWorkOrderStatus? ParseStatus(string? value) =>
        Enum.TryParse<ProductionWorkOrderStatus>(value, true, out var status) ? status : null;
}
