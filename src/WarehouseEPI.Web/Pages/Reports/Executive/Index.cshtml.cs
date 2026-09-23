using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Executive;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(
    ExecutiveReportService executiveService,
    ReportExportService exportService,
    WarehouseClock clock,
    TimeProvider timeProvider) : PageModel
{
    public ExecutiveReportDto Report { get; private set; } = null!;

    [BindProperty(SupportsGet = true)] public string? Period { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var filter = await BuildFilterAsync(cancellationToken);
        Report = await executiveService.GetExecutiveReportAsync(filter, cancellationToken);
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken cancellationToken)
    {
        if (!User.IsInRole("ADMIN")) return Forbid();
        var filter = await BuildFilterAsync(cancellationToken);
        var report = await executiveService.GetExecutiveReportAsync(filter, cancellationToken);
        var excel = await exportService.ExportExecutiveToExcelAsync(report, cancellationToken);
        return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Informe_Ejecutivo_{report.GeneratedAtLocal:yyyy-MM-dd}.xlsx");
    }

    private async Task<ExecutiveReportFilter> BuildFilterAsync(CancellationToken cancellationToken)
    {
        var today = await clock.GetDateAsync(timeProvider.GetUtcNow(), cancellationToken);
        var period = ResolvePeriod(today, Period, From, To);
        Period = period.Code;
        From = period.From;
        To = period.To;
        var current = await clock.GetUtcIntervalAsync(period.From, period.To, cancellationToken);
        var previous = await clock.GetUtcIntervalAsync(period.PreviousFrom, period.PreviousTo, cancellationToken);
        return new ExecutiveReportFilter(
            current.FromInclusive, current.ToExclusive, period.Label,
            previous.FromInclusive, previous.ToExclusive, period.PreviousLabel);
    }

    public static ExecutivePeriod ResolvePeriod(DateOnly today, string? requestedPeriod, DateOnly? requestedFrom, DateOnly? requestedTo)
    {
        var code = requestedPeriod?.Trim().ToLowerInvariant();
        DateOnly from;
        DateOnly to;
        DateOnly previousFrom;
        DateOnly previousTo;
        string label;
        string previousLabel;

        if (code == "last-month")
        {
            var thisMonth = new DateOnly(today.Year, today.Month, 1);
            to = thisMonth.AddDays(-1);
            from = new DateOnly(to.Year, to.Month, 1);
            previousTo = from.AddDays(-1);
            previousFrom = new DateOnly(previousTo.Year, previousTo.Month, 1);
            label = $"Mes pasado ({from:MMMM yyyy})";
            previousLabel = $"Mes anterior ({previousFrom:MMMM yyyy})";
        }
        else if (code == "this-week")
        {
            var daysFromMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            from = today.AddDays(-daysFromMonday);
            to = today;
            previousFrom = from.AddDays(-7);
            previousTo = to.AddDays(-7);
            label = "Esta semana";
            previousLabel = "Mismos días de la semana anterior";
        }
        else if (code == "last-week")
        {
            var daysFromMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            var thisMonday = today.AddDays(-daysFromMonday);
            from = thisMonday.AddDays(-7);
            to = thisMonday.AddDays(-1);
            previousFrom = from.AddDays(-7);
            previousTo = to.AddDays(-7);
            label = "Semana pasada";
            previousLabel = "Semana anterior";
        }
        else if (code == "custom" && requestedFrom.HasValue && requestedTo.HasValue)
        {
            from = requestedFrom.Value <= requestedTo.Value ? requestedFrom.Value : requestedTo.Value;
            to = requestedFrom.Value <= requestedTo.Value ? requestedTo.Value : requestedFrom.Value;
            var days = to.DayNumber - from.DayNumber + 1;
            previousTo = from.AddDays(-1);
            previousFrom = previousTo.AddDays(-(days - 1));
            label = $"{from:dd/MM/yyyy} a {to:dd/MM/yyyy}";
            previousLabel = $"{previousFrom:dd/MM/yyyy} a {previousTo:dd/MM/yyyy}";
        }
        else
        {
            code = "this-month";
            from = new DateOnly(today.Year, today.Month, 1);
            to = today;
            previousFrom = from.AddMonths(-1);
            var comparableDay = Math.Min(today.Day, DateTime.DaysInMonth(previousFrom.Year, previousFrom.Month));
            previousTo = new DateOnly(previousFrom.Year, previousFrom.Month, comparableDay);
            label = $"Mes actual ({today:MMMM yyyy})";
            previousLabel = $"Mismos días de {previousFrom:MMMM yyyy}";
        }

        return new(code, from, to, label, previousFrom, previousTo, previousLabel);
    }
}

public sealed record ExecutivePeriod(
    string Code,
    DateOnly From,
    DateOnly To,
    string Label,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    string PreviousLabel);
