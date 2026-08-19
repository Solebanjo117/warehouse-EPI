using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Wip;

public sealed class DetailsModel(WipReportService reportService, WarehouseDbContext dbContext, WarehouseClock clock) : PageModel
{
    public WipIssueRow Issue { get; private set; } = null!;
    public IReadOnlyList<DispositionRow> Dispositions { get; private set; } = [];
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        var issue = await reportService.GetIssueAsync(id, token);
        if (issue is null) return NotFound();
        Issue = issue;
        var rows = await dbContext.WipDispositions.AsNoTracking().Where(item => item.OriginalMovementLineId == id)
            .OrderBy(item => item.OccurredAt).Select(item => new DispositionRow(item.Id, item.Type.ToString(), item.Quantity,
                item.ResponsibleUser.FullName, item.Reference, item.Notes, item.OccurredAt,
                item.ReversesDispositionId != null, dbContext.WipDispositions.Any(reverse => reverse.ReversesDispositionId == item.Id)))
            .ToListAsync(token);
        Dispositions = [];
        var localized = new List<DispositionRow>();
        foreach (var row in rows) localized.Add(row with { OccurredAt = await clock.ConvertAsync(row.OccurredAt, token) });
        Dispositions = localized;
        return Page();
    }
    public sealed record DispositionRow(Guid Id, string Type, decimal Quantity, string Responsible, string? Reference,
        string? Notes, DateTimeOffset OccurredAt, bool IsReversal, bool WasReversed);
}
