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
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService,
    TimeProvider timeProvider) : PageModel
{
    private const int PageSize = 25;
    public WipReportPage Report { get; private set; } = new([], [], 0, 1, PageSize);
    public WipTrackedReportPage TrackedReport { get; private set; } = new([], [], 0, 1, PageSize);
    public IReadOnlyList<WipAreaOption> WipAreas { get; private set; } = [];
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }
    public string? Search { get; private set; }
    public Guid? WipAreaId { get; private set; }
    public string? Attention { get; private set; }
    public int WipReminderDays { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TrackedReport.TotalActivityCount / (double)PageSize));
    public IReadOnlyList<int> VisiblePages { get; private set; } = [];

    public async Task OnGetAsync(DateOnly? from, DateOnly? to, string? search, Guid? wipAreaId, string? attention,
        int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var today = await clock.GetDateAsync(now, cancellationToken);
        var mondayOffset = ((int)today.DayOfWeek + 6) % 7;
        Attention = string.Equals(attention, "aged", StringComparison.OrdinalIgnoreCase) ? "aged" : null;
        From = from ?? (Attention is null ? today.AddDays(-mondayOffset) : null);
        To = to ?? (Attention is null ? From!.Value.AddDays(6) : null);
        Search = search?.Trim();
        WipAreaId = wipAreaId;
        var settings = await settingsService.GetAsync(cancellationToken);
        WipReminderDays = settings.WipReminderDays;
        var interval = await clock.GetUtcIntervalAsync(From, To, cancellationToken);
        var filter = new WipReportFilter(interval.FromInclusive, interval.ToExclusive, Search, WipAreaId,
            AgedBefore: Attention is null ? null : now.AddDays(-WipReminderDays));
        TrackedReport = await reportService.GetTrackedPageAsync(filter, pageNumber, PageSize, cancellationToken);
        Report = await reportService.GetPageAsync(
            new(interval.FromInclusive, interval.ToExclusive, Search, WipAreaId),
            pageNumber, PageSize, cancellationToken);
        var currentPage = Math.Min(TrackedReport.PageNumber, TotalPages);
        var firstVisible = Math.Max(1, currentPage - 2);
        var lastVisible = Math.Min(TotalPages, currentPage + 2);
        VisiblePages = Enumerable.Range(firstVisible, lastVisible - firstVisible + 1).ToArray();
        WipAreas = await dbContext.Locations.AsNoTracking()
            .Where(location => location.IsPhysicallyPresent &&
                location.OperationalRole == LocationOperationalRole.Wip && location.IsActive)
            .OrderBy(location => location.Code)
            .Select(location => new WipAreaOption(location.Id, location.Code))
            .ToListAsync(cancellationToken);
    }

    public sealed record WipAreaOption(Guid Id, string Code);
}
