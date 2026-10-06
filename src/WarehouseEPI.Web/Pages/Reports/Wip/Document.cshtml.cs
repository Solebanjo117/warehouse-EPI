using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Reports.Wip;

public sealed class DocumentModel(WarehouseDbContext db, WarehouseClock clock) : PageModel
{
    public WipDocument Document { get; private set; } = null!;
    public DateTimeOffset Date { get; private set; }
    public decimal Pending { get; private set; }
    public IReadOnlyList<ApplicationRow> Applications { get; private set; } = [];
    public int PageNumber { get; private set; }
    public int TotalPages { get; private set; }
    public async Task<IActionResult> OnGetAsync(Guid id, int pageNumber = 1, CancellationToken token = default)
    {
        var document = await db.WipDocuments.AsNoTracking().Include(x => x.Product).ThenInclude(x => x.BaseUnit)
            .Include(x => x.WipLocation).Include(x => x.ResponsibleUser).Include(x => x.MovementLine).ThenInclude(x => x!.Movement)
            .Include(x => x.Lots).ThenInclude(x => x.Lot).SingleOrDefaultAsync(x => x.Id == id, token);
        if (document is null) return NotFound();
        Document = document;
        Date = await clock.ConvertAsync(document.OccurredAt, token);
        var query = db.WipDocumentApplications.AsNoTracking().Where(x => x.DocumentId == id);
        Pending = document.Quantity - await query.Where(x => x.Kind != WipDocumentApplicationKind.Reversal &&
            !db.WipDocumentApplications.Any(r => r.ReversesApplicationId == x.Id)).SumAsync(x => x.Quantity, token);
        TotalPages = Math.Max(1, (int)Math.Ceiling(await query.CountAsync(token) / 25d));
        PageNumber = Math.Clamp(pageNumber, 1, TotalPages);
        var rows = await query.OrderByDescending(x => x.RecordedAt).ThenBy(x => x.Id).Skip((PageNumber - 1) * 25).Take(25)
            .Select(x => new
            {
                x.OperationId,
                x.Kind,
                x.Quantity,
                x.RecordedAt,
                x.Reference,
                x.Notes,
                Reversed = db.WipDocumentApplications.Any(r => r.ReversesApplicationId == x.Id),
                LinkedToOrder = x.IssueLinkId != null
            }).ToListAsync(token);
        var applications = new List<ApplicationRow>();
        foreach (var row in rows) applications.Add(new(row.OperationId, row.Kind, row.Quantity,
            await clock.ConvertAsync(row.RecordedAt, token), row.Reversed, row.Reference, row.Notes, row.LinkedToOrder));
        Applications = applications;
        return Page();
    }
    public sealed record ApplicationRow(Guid OperationId, WipDocumentApplicationKind Kind, decimal Quantity,
        DateTimeOffset Date, bool Reversed, string? Reference, string? Notes, bool LinkedToOrder);
}
