using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Trace;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(UnifiedTraceService trace, UnifiedTraceExportService exports, WarehouseClock clock, WarehouseSettingsService settings) : PageModel
{
    public UnifiedTracePage Results { get; private set; } = new([], 0);
    public Dictionary<string, DateTimeOffset> LocalDates { get; } = [];
    public string? Search { get; private set; }
    public string? Kind { get; private set; }
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }
    public string TimeZoneId { get; private set; } = string.Empty;
    public int PageNumber { get; private set; } = 1;
    public const int PageSize = 25;

    public async Task OnGetAsync(string? search, string? kind, DateOnly? from, DateOnly? to, int pageNumber = 1, CancellationToken token = default)
    {
        await SetStateAsync(search, kind, from, to, pageNumber, token);
        var interval = await clock.GetUtcIntervalAsync(From, To, token);
        Results = await trace.SearchAsync(new(interval.FromInclusive, interval.ToExclusive, Search, Kind, PageNumber, PageSize), token);
        foreach (var item in Results.Items) LocalDates[item.Id] = await clock.ConvertAsync(item.OccurredAt, token);
    }

    public async Task<IActionResult> OnGetExportAsync(string? format, string? search, string? kind, DateOnly? from, DateOnly? to, CancellationToken token = default)
    {
        await SetStateAsync(search, kind, from, to, 1, token);
        var interval = await clock.GetUtcIntervalAsync(From, To, token);
        var filter = new UnifiedTraceFilter(interval.FromInclusive, interval.ToExclusive, Search, Kind, 1, 10000);
        var batch = await trace.ExportAsync(filter, 10000, token);
        if (batch.ExceedsLimit) return BadRequest($"La trazabilidad contiene {batch.TotalRows:N0} filas y supera el límite de {batch.MaximumRows:N0}. Aplica filtros más específicos.");
        var local = await clock.ConvertAsync(DateTimeOffset.UtcNow, token); var name=$"trazabilidad-{local:yyyyMMdd-HHmmss}";
        if (string.Equals(format,"xlsx",StringComparison.OrdinalIgnoreCase)) return File(await exports.ToExcelAsync(batch.Items,filter,token),"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",$"{name}.xlsx");
        return File(await exports.ToCsvAsync(batch.Items,filter,token),"text/csv; charset=utf-8",$"{name}.csv");
    }

    private async Task SetStateAsync(string? search,string? kind,DateOnly? from,DateOnly? to,int page,CancellationToken token)
    {
        Search=search?.Trim();Kind=kind is "movement" or "document" or "wip" or "count"?kind:null;PageNumber=Math.Max(1,page);TimeZoneId=(await settings.GetAsync(token)).TimeZoneId;
        var today=await clock.GetDateAsync(DateTimeOffset.UtcNow,token);From=from??today.AddDays(-29);To=to??today;
        if(From>To)(From,To)=(To,From);
    }
}
