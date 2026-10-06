using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class AreasModel(WarehouseDbContext db) : PageModel
{
    public IReadOnlyList<Location> Areas { get; private set; } = [];
    public string? Search { get; private set; }
    public string Status { get; private set; } = "all";
    public int CurrentPage { get; private set; }
    public int TotalPages { get; private set; }
    public IEnumerable<int> VisiblePages => Enumerable.Range(Math.Max(1, CurrentPage - 2),
        Math.Min(TotalPages, CurrentPage + 2) - Math.Max(1, CurrentPage - 2) + 1);
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(string? search, string status = "all", int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        Search = search?.Trim();
        Status = status is "active" or "inactive" or "blocked" ? status : "all";
        var query = db.Locations.AsNoTracking().Where(x => x.Kind == LocationKind.Area);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.ToUpperInvariant();
            query = query.Where(x => x.Code.Contains(term) ||
                (x.Description != null && x.Description.ToUpper().Contains(term)));
        }
        query = Status switch
        {
            "active" => query.Where(x => x.IsActive && !x.IsBlocked && x.IsPhysicallyPresent),
            "inactive" => query.Where(x => !x.IsActive),
            "blocked" => query.Where(x => x.IsBlocked),
            _ => query
        };
        TotalPages = Math.Max(1, (int)Math.Ceiling(await query.CountAsync(cancellationToken) / 25d));
        CurrentPage = Math.Clamp(pageNumber, 1, TotalPages);
        Areas = await query.OrderBy(x => x.Code).Skip((CurrentPage - 1) * 25).Take(25)
            .ToListAsync(cancellationToken);
    }
}
