using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Trace;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(
    UnifiedTraceService trace,
    UnifiedTraceExportService exports,
    WarehouseClock clock,
    WarehouseSettingsService settings,
    TimeProvider? timeProvider = null) : PageModel
{
    public UnifiedTracePage Results { get; private set; } = new([], 0);
    public Dictionary<string, DateTimeOffset> LocalDates { get; } = [];
    public string? Search { get; private set; }
    public string? Kind { get; private set; }
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }
    public string Period { get; private set; } = "30";
    public string TimeZoneId { get; private set; } = string.Empty;
    public int PageNumber { get; private set; } = 1;
    public const int PageSize = 25;

    public async Task OnGetAsync(string? search, string? kind, DateOnly? from, DateOnly? to, string? period = null, int pageNumber = 1, CancellationToken token = default)
    {
        await SetStateAsync(search, kind, from, to, period, pageNumber, token);
        var interval = await clock.GetUtcIntervalAsync(From, To, token);
        Results = await trace.SearchAsync(new(interval.FromInclusive, interval.ToExclusive, Search, Kind, PageNumber, PageSize), token);
        foreach (var item in Results.Items) LocalDates[item.Id] = await clock.ConvertAsync(item.OccurredAt, token);
    }

    public async Task<IActionResult> OnGetExportAsync(string? format, string? search, string? kind, DateOnly? from, DateOnly? to, string? period = null, CancellationToken token = default)
    {
        await SetStateAsync(search, kind, from, to, period, 1, token);
        var interval = await clock.GetUtcIntervalAsync(From, To, token);
        var filter = new UnifiedTraceFilter(interval.FromInclusive, interval.ToExclusive, Search, Kind, 1, 10000);
        var batch = await trace.ExportAsync(filter, 10000, token);
        if (batch.ExceedsLimit) return BadRequest($"La trazabilidad contiene {batch.TotalRows:N0} filas y supera el límite de {batch.MaximumRows:N0}. Aplica filtros más específicos.");
        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var local = await clock.ConvertAsync(nowUtc, token); var name = $"trazabilidad-{local:yyyyMMdd-HHmmss}";
        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase)) return File(await exports.ToExcelAsync(batch.Items, filter, token), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}.xlsx");
        return File(await exports.ToCsvAsync(batch.Items, filter, token), "text/csv; charset=utf-8", $"{name}.csv");
    }

    private async Task SetStateAsync(string? search, string? kind, DateOnly? from, DateOnly? to, string? period, int page, CancellationToken token)
    {
        Search = search?.Trim();
        Kind = kind is "movement" or "document" or "wip" or "count" ? kind : null;
        PageNumber = Math.Max(1, page);
        TimeZoneId = (await settings.GetAsync(token)).TimeZoneId;

        var normalizedPeriod = period is "today" or "yesterday" or "this-week" or "last-week" or "7" or "this-month" or "last-month" or "30" or "all" or "custom"
            ? period
            : null;
        Period = normalizedPeriod ?? (from is not null || to is not null ? "custom" : "30");

        var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var today = await clock.GetDateAsync(nowUtc, token);

        if (Period == "all")
        {
            from = null;
            to = null;
        }
        else if (Period != "custom")
        {
            if (Period == "today")
            {
                from = today;
                to = today;
            }
            else if (Period == "yesterday")
            {
                from = today.AddDays(-1);
                to = today.AddDays(-1);
            }
            else if (Period == "this-week")
            {
                var daysToMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                from = today.AddDays(-daysToMonday);
                to = today;
            }
            else if (Period == "last-week")
            {
                var daysToMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                var startOfThisWeek = today.AddDays(-daysToMonday);
                from = startOfThisWeek.AddDays(-7);
                to = startOfThisWeek.AddDays(-1);
            }
            else if (Period == "7")
            {
                from = today.AddDays(-6);
                to = today;
            }
            else if (Period == "this-month")
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
            else // "30"
            {
                from = today.AddDays(-29);
                to = today;
            }
        }

        From = from;
        To = to;
        if (From.HasValue && To.HasValue && From.Value > To.Value)
            (From, To) = (To, From);
    }
}
