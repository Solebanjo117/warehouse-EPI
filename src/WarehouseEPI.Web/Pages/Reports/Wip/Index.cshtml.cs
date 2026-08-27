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
    public IReadOnlyList<WipAreaOption> WipAreas { get; private set; } = [];
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }
    public string? Search { get; private set; }
    public Guid? WipAreaId { get; private set; }
    public string? Attention { get; private set; }
    public int WipReminderDays { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Report.TotalCount / (double)PageSize));

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
        Report = await reportService.GetPageAsync(
            new(interval.FromInclusive, interval.ToExclusive, Search, WipAreaId,
                AgedBefore: Attention is null ? null : now.AddDays(-WipReminderDays),
                RequireNoEffectiveReturn: Attention is not null),
            pageNumber, PageSize, cancellationToken);
        WipAreas = await dbContext.Locations.AsNoTracking()
            .Where(location => location.OperationalRole == LocationOperationalRole.Wip && location.IsActive)
            .OrderBy(location => location.Code)
            .Select(location => new WipAreaOption(location.Id, location.Code))
            .ToListAsync(cancellationToken);
    }

    public sealed record WipAreaOption(Guid Id, string Code);
}
