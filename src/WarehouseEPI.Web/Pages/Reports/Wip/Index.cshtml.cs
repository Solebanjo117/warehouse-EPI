using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Wip;

public sealed class IndexModel(
    WipReportService reportService,
    WarehouseClock clock,
    WarehouseDbContext dbContext) : PageModel
{
    private const int PageSize = 25;
    public WipReportPage Report { get; private set; } = new([], [], 0, 1, PageSize);
    public IReadOnlyList<WipAreaOption> WipAreas { get; private set; } = [];
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public string? Search { get; private set; }
    public Guid? WipAreaId { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Report.TotalCount / (double)PageSize));

    public async Task OnGetAsync(DateOnly? from, DateOnly? to, string? search, Guid? wipAreaId,
        int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        var today = await clock.GetDateAsync(DateTimeOffset.UtcNow, cancellationToken);
        var mondayOffset = ((int)today.DayOfWeek + 6) % 7;
        From = from ?? today.AddDays(-mondayOffset);
        To = to ?? From.AddDays(6);
        Search = search?.Trim();
        WipAreaId = wipAreaId;
        var interval = await clock.GetUtcIntervalAsync(From, To, cancellationToken);
        Report = await reportService.GetPageAsync(
            new(interval.FromInclusive, interval.ToExclusive, Search, WipAreaId),
            pageNumber, PageSize, cancellationToken);
        WipAreas = await dbContext.Locations.AsNoTracking()
            .Where(location => location.OperationalRole == LocationOperationalRole.Wip && location.IsActive)
            .OrderBy(location => location.Code)
            .Select(location => new WipAreaOption(location.Id, location.Code))
            .ToListAsync(cancellationToken);
    }

    public sealed record WipAreaOption(Guid Id, string Code);
}
