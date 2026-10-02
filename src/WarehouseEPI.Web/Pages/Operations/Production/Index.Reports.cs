using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed partial class IndexModel
{
    // Other tabs do not send report filters; normalize optional values only when loading reports.
    [BindProperty(SupportsGet = true)] public string? ReportKind { get; set; } = "daily";
    [BindProperty(SupportsGet = true)] public string? ReportPeriod { get; set; } = "day";
    [BindProperty(SupportsGet = true)] public int ReportPage { get; set; } = 1;
    public ProductionReport? Report { get; private set; }
    public ProductionReportTable? ReportTable { get; private set; }
    public DateTimeOffset ReportGeneratedAt { get; private set; }
    public bool ReportPrint { get; private set; }
    public int ReportPages => Math.Max(1, (int)Math.Ceiling((Report?.Rows.Count ?? 0) / 25d));
    public IEnumerable<ProductionReportTableRow> ReportRows => ReportTable is null ? [] :
        ReportPrint || Report?.Filter.Products != true ? ReportTable.Rows : ReportTable.Rows.Skip((ReportPage - 1) * 25).Take(25);
    public string ReportTitle => ReportKind == "products" ? "Avance y pendientes por producto" : "Resumen diario por proceso";

    private async Task LoadReportAsync(CancellationToken token)
    {
        ReportKind = ReportKind == "products" ? "products" : "daily";
        ReportPeriod = ReportPeriod == "week" ? "week" : "day";
        if (Area.HasValue && !Enum.IsDefined(Area.Value)) Area = null;
        var week = Weeks.SingleOrDefault(x => x.Id == WeekId);
        if (week is null)
        {
            ModelState.AddModelError(string.Empty, texts["La semana seleccionada ya no existe. Selecciona otra semana."]);
            return;
        }
        Report = await reports.GetAsync(new(week.Id, Through ?? (Today >= week.WeekStart && Today <= week.WeekEnd ? Today : week.WeekStart),
            ReportKind == "products", ReportPeriod == "week", Sku, Reference, Area), token);
        if (Report is null) return;
        Through = Report.Filter.Date;
        Sku = Report.Filter.Sku;
        Reference = Report.Filter.Reference;
        ReportTable = ProductionReportTable.Create(Report);
        ReportPage = Math.Clamp(ReportPage, 1, ReportPages);
        foreach (var key in new[] { nameof(Through), nameof(Area), nameof(ReportKind), nameof(ReportPeriod), nameof(ReportPage) }) ModelState.Remove(key);
        ReportGeneratedAt = await clock.ConvertAsync(timeProvider.GetUtcNow(), token);
    }

    public Dictionary<string, string> ReportRoute(string? kind = null, DateOnly? date = null, int? page = null) => new()
    {
        ["Tab"] = "reports", ["WeekId"] = WeekId?.ToString() ?? "", ["Through"] = (date ?? Through)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        ["ReportKind"] = kind ?? ReportKind ?? "daily", ["ReportPeriod"] = date.HasValue ? "day" : ReportPeriod ?? "day",
        ["Sku"] = Sku ?? "", ["Reference"] = Reference ?? "", ["Area"] = Area?.ToString() ?? "",
        ["ReportPage"] = (page ?? ReportPage).ToString(CultureInfo.InvariantCulture)
    };

    public async Task<IActionResult> OnGetReportExportAsync(CancellationToken token)
    {
        Tab = "reports";
        await LoadAsync(token);
        if (Report is null) return NotFound();
        if (!Report.CanOutput) return BadRequest(texts["El reporte supera 10.000 filas. Usa filtros más específicos."].Value);
        var bytes = ProductionReportExportService.Export(Report, ReportGeneratedAt, key => texts[key].Value);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Produccion-{ReportKind}-{Report.WeekStart:yyyy-MM-dd}.xlsx");
    }

    public async Task<IActionResult> OnGetReportPrintAsync(CancellationToken token)
    {
        Tab = "reports";
        await LoadAsync(token);
        if (Report is null) return NotFound();
        if (!Report.CanOutput) return BadRequest(texts["El reporte supera 10.000 filas. Usa filtros más específicos."].Value);
        ReportPrint = true;
        return Page();
    }
}
