using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Workload;

public sealed class IndexModel(
    WorkQueueService workQueueService,
    WorkloadReportService workloadService,
    ReportExportService exportService,
    WarehouseClock clock,
    WarehouseDbContext dbContext) : PageModel
{
    public WorkQueueSnapshotDto? Queue { get; private set; }
    public WorkloadReportDto? Report { get; private set; }
    public bool IsAdmin => User.IsInRole("ADMIN");

    [BindProperty(SupportsGet = true)]
    public string? View { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? QueueSearch { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Period { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Shift { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? MovementType { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<SelectListItem> UserOptions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        View = string.Equals(View, "activity", StringComparison.OrdinalIgnoreCase) ? "activity" : "pending";
        if (View == "pending")
        {
            QueueSearch = string.IsNullOrWhiteSpace(QueueSearch) ? null : QueueSearch.Trim();
            Queue = await workQueueService.GetSnapshotAsync(new WorkQueueFilter(QueueSearch), IsAdmin, cancellationToken);
            return;
        }

        if (IsAdmin)
            await LoadUserOptionsAsync(cancellationToken);
        else
        {
            UserId = null;
            Search = null;
        }

        var (filter, periodLabel) = await BuildFilterAsync(cancellationToken);
        Report = await workloadService.GetWorkloadPageAsync(filter, periodLabel, IsAdmin, cancellationToken);
    }

    public async Task<IActionResult> OnGetExportAsync(
        string? format,
        CancellationToken cancellationToken)
    {
        if (!User.IsInRole("ADMIN"))
            return Forbid();

        var (filter, periodLabel) = await BuildFilterAsync(cancellationToken);
        var exportData = await workloadService.GetWorkloadExportAsync(filter, cancellationToken);

        var dateStamp = exportData.GeneratedAtLocal.ToString("yyyy-MM-dd");

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = await exportService.ExportWorkloadToCsvAsync(
                exportData.Operators, exportData.Summary, filter, periodLabel, cancellationToken);
            return File(csv, "text/csv; charset=utf-8", $"Carga_Trabajo_{dateStamp}.csv");
        }

        var excel = await exportService.ExportWorkloadToExcelAsync(
            exportData.Operators, exportData.Summary, filter, periodLabel, cancellationToken);
        return File(excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Carga_Trabajo_{dateStamp}.xlsx");
    }

    private async Task<(WorkloadReportFilter Filter, string PeriodLabel)> BuildFilterAsync(CancellationToken cancellationToken)
    {
        var normalizedShift = ParseShift(Shift);
        var normalizedType = ParseMovementType(MovementType);
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        PageNumber = Math.Max(1, PageNumber);

        var today = await clock.GetDateAsync(DateTimeOffset.UtcNow, cancellationToken);
        DateOnly fromDate;
        DateOnly toDate;
        string periodLabel;

        var normalizedPeriod = Period?.Trim().ToLowerInvariant();
        if (normalizedPeriod == "today")
        {
            fromDate = today;
            toDate = today;
            periodLabel = "Hoy";
        }
        else if (normalizedPeriod == "yesterday")
        {
            fromDate = today.AddDays(-1);
            toDate = today.AddDays(-1);
            periodLabel = "Ayer";
        }
        else if (normalizedPeriod == "this-week")
        {
            var diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
            fromDate = today.AddDays(-diff);
            toDate = today;
            periodLabel = "Esta semana";
        }
        else if (normalizedPeriod == "last-week")
        {
            var diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
            var thisMonday = today.AddDays(-diff);
            fromDate = thisMonday.AddDays(-7);
            toDate = thisMonday.AddDays(-1);
            periodLabel = "Semana pasada";
        }
        else if (normalizedPeriod == "this-month")
        {
            fromDate = new DateOnly(today.Year, today.Month, 1);
            toDate = today;
            periodLabel = "Este mes";
        }
        else if (normalizedPeriod == "last-month")
        {
            var firstOfThis = new DateOnly(today.Year, today.Month, 1);
            toDate = firstOfThis.AddDays(-1);
            fromDate = new DateOnly(toDate.Year, toDate.Month, 1);
            periodLabel = "Mes pasado";
        }
        else if (normalizedPeriod == "custom" && From.HasValue && To.HasValue)
        {
            fromDate = From.Value <= To.Value ? From.Value : To.Value;
            toDate = From.Value <= To.Value ? To.Value : From.Value;
            periodLabel = $"{fromDate:dd/MM/yyyy} a {toDate:dd/MM/yyyy}";
        }
        else // default: 30 days
        {
            Period = "30";
            fromDate = today.AddDays(-29);
            toDate = today;
            periodLabel = "Últimos 30 días";
        }

        From = fromDate;
        To = toDate;

        var interval = await clock.GetUtcIntervalAsync(fromDate, toDate, cancellationToken);
        var filter = new WorkloadReportFilter(
            interval.FromInclusive,
            interval.ToExclusive,
            normalizedShift,
            UserId,
            normalizedType,
            Search,
            PageNumber,
            PageSize: 25);

        return (filter, periodLabel);
    }

    private static WorkloadShift ParseShift(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "morning" => WorkloadShift.Morning,
            "afternoon" => WorkloadShift.Afternoon,
            "night" => WorkloadShift.Night,
            _ => WorkloadShift.All
        };

    private static InventoryMovementType? ParseMovementType(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "entry" => InventoryMovementType.Entry,
            "exit" => InventoryMovementType.Exit,
            "transfer" => InventoryMovementType.Transfer,
            "adjustment" => InventoryMovementType.Adjustment,
            _ => null
        };

    private async Task LoadUserOptionsAsync(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users.AsNoTracking()
            .OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName, u.IsActive })
            .ToListAsync(cancellationToken);

        UserOptions = users.Select(u => new SelectListItem(
            u.IsActive ? u.FullName : $"{u.FullName} (inactivo)",
            u.Id.ToString(),
            u.Id == UserId)).ToList();
    }
}
