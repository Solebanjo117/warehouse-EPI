using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Operations.Incidents;

public sealed class IndexModel(MaterialIncidentQuery query, WarehouseClock clock, WarehouseDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? ProductCode { get; set; }
    [BindProperty(SupportsGet = true)] public string? LocationCode { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public MaterialIncidentScope? Scope { get; set; }
    [BindProperty(SupportsGet = true)] public MaterialIncidentKind? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ProductId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? LocationId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? PlateId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ArrivalLineId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Relation { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public IncidentPage Results { get; private set; } = new([], false);
    public IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> LocalTimes { get; private set; } = new Dictionary<DateTimeOffset, DateTimeOffset>();
    public Dictionary<string, string> Routes(int page) => new() { ["productCode"] = ProductCode ?? "", ["locationCode"] = LocationCode ?? "", ["search"] = Search ?? "", ["status"] = Status ?? "", ["scope"] = Scope?.ToString() ?? "", ["kind"] = Kind?.ToString() ?? "",
        ["productId"] = ProductId?.ToString() ?? "", ["locationId"] = LocationId?.ToString() ?? "", ["plateId"] = PlateId?.ToString() ?? "", ["arrivalLineId"] = ArrivalLineId?.ToString() ?? "", ["relation"] = Relation ?? "", ["pageNumber"] = page.ToString() };
    public async Task OnGetAsync(CancellationToken ct)
    {
        if (ProductId.HasValue && string.IsNullOrWhiteSpace(ProductCode))
        {
            ProductCode = await db.Products.Where(p => p.Id == ProductId).Select(p => p.Sku).SingleOrDefaultAsync(ct);
            ModelState.Remove(nameof(ProductCode));
        }
        if (LocationId.HasValue && Relation != "current" && string.IsNullOrWhiteSpace(LocationCode))
        {
            LocationCode = await db.Locations.Where(l => l.Id == LocationId).Select(l => l.Code).SingleOrDefaultAsync(ct);
            ModelState.Remove(nameof(LocationCode));
        }
        PageNumber = Math.Clamp(PageNumber, 1, 100000);
        Results = await query.ListAsync(new(Search, Status, Scope, Kind, ProductId, LocationId, PlateId, ArrivalLineId, Relation, PageNumber, ProductCode, LocationCode), ct);
        LocalTimes = await clock.ConvertManyAsync(Results.Items.Select(i => i.ReportedAt), ct);
    }

    public async Task<IActionResult> OnGetProductsAsync(string? q, CancellationToken ct)
    {
        var term = q?.Trim() ?? "";
        if (term.Length == 0) return new JsonResult(Array.Empty<object>());
        var normalized = term.ToUpperInvariant();
        return new JsonResult(await db.Products.AsNoTracking()
            .WhereProductText(term, p => p.Sku.ToUpper().Contains(normalized) ||
                (p.Description != null && p.Description.ToUpper().Contains(normalized)) ||
                (p.ExternalReference != null && p.ExternalReference.ToUpper().Contains(normalized)) ||
                p.Barcodes.Any(b => b.IsActive && b.Barcode.ToUpper().Contains(normalized)))
            .OrderByDescending(p => p.Sku.ToUpper() == normalized).ThenBy(p => p.Sku).Take(12)
            .Select(p => new { p.Id, Code = p.Sku, p.Description, p.ExternalReference, p.IsActive }).ToListAsync(ct));
    }

    public async Task<IActionResult> OnGetLocationsAsync(string? q, CancellationToken ct)
    {
        var term = q?.Trim().ToUpperInvariant() ?? "";
        if (term.Length == 0) return new JsonResult(Array.Empty<object>());
        return new JsonResult(await db.Locations.AsNoTracking()
            .Where(l => l.OperationalRole != LocationOperationalRole.Wip &&
                (l.Code.ToUpper().Contains(term) || l.Description != null && l.Description.ToUpper().Contains(term)))
            .OrderByDescending(l => l.Code.ToUpper() == term).ThenBy(l => l.Code).Take(12)
            .Select(l => new { l.Id, l.Code, l.Description, l.IsActive, l.IsBlocked }).ToListAsync(ct));
    }

    public async Task<IActionResult> OnGetSingleStockProductAsync(Guid locationId, CancellationToken ct)
    {
        if (!await db.Locations.AnyAsync(l => l.Id == locationId && l.OperationalRole != LocationOperationalRole.Wip, ct))
            return NotFound();
        // Incidents include historical catalogs; the operational lookup excludes them.
        var stockedIds = db.InventoryBalances.Where(b => b.LocationId == locationId)
            .GroupBy(b => b.ProductId).Where(g => g.Sum(b => b.Quantity) > 0).Select(g => g.Key);
        var products = await db.Products.AsNoTracking().Where(p => stockedIds.Contains(p.Id))
            .OrderBy(p => p.Id).Select(p => new { p.Id, Code = p.Sku }).Take(2).ToListAsync(ct);
        return new JsonResult(products.Count == 1 ? products[0] : null);
    }

    public async Task<IActionResult> OnGetSingleStockLocationAsync(Guid productId, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(p => p.Id == productId, ct)) return NotFound();
        var stockedIds = db.InventoryBalances.Where(b => b.ProductId == productId)
            .GroupBy(b => b.LocationId).Where(g => g.Sum(b => b.Quantity) > 0).Select(g => g.Key);
        var locations = await db.Locations.AsNoTracking()
            .Where(l => l.OperationalRole != LocationOperationalRole.Wip && stockedIds.Contains(l.Id))
            .OrderBy(l => l.Id).Select(l => new { l.Id, l.Code }).Take(2).ToListAsync(ct);
        return new JsonResult(locations.Count == 1 ? locations[0] : null);
    }
}
