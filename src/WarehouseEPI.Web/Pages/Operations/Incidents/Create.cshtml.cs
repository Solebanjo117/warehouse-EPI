using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Operations.Incidents;

[RequestSizeLimit(30 * 1024 * 1024)]
public sealed class CreateModel(MaterialIncidentService service, WarehouseDbContext db) : IncidentCaptureModel
{
    [BindProperty(SupportsGet = true)] public Guid? ArrivalLineId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? PlateId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ProductId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? LocationId { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string ListUrl => Url.IsLocalUrl(ReturnUrl) &&
        string.Equals(ReturnUrl!.Split('?', '#')[0].TrimEnd('/'), Url.Page("Index")!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        ? ReturnUrl! : Url.Page("Index")!;
    public bool NeedsInitialContext => Context is null && !LocationId.HasValue;
    [BindProperty] public Guid OperationId { get; set; } = Guid.NewGuid();
    [BindProperty] public string ContextToken { get; set; } = "";
    [BindProperty] public MaterialIncidentScope Scope { get; set; }
    [BindProperty] public MaterialIncidentKind Kind { get; set; }
    [BindProperty] public MaterialIncidentDifference Difference { get; set; }
    [BindProperty] public decimal? Quantity { get; set; }
    [BindProperty] public string Description { get; set; } = "";
    public IncidentContext? Context { get; private set; }
    public IReadOnlyList<Guid> Plates { get; private set; } = [];
    public IReadOnlyList<Location> Locations { get; private set; } = [];
    public IncidentContextRequest RequestContext => new(ArrivalLineId, PlateId, ProductId, LocationId);
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest();
        Scope = ArrivalLineId.HasValue ? MaterialIncidentScope.Receiving : MaterialIncidentScope.Undetermined;
        if (!await LoadAsync(ct)) return NotFound();
        ContextToken = Context?.Token ?? "";
        return Page();
    }
    public async Task<IActionResult> OnGetProductsAsync(Guid? locationId, string? q, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest();
        if (locationId.HasValue && !await db.Locations.AnyAsync(l => l.Id == locationId && l.OperationalRole != LocationOperationalRole.Wip, ct)) return NotFound();
        var term = q?.Trim().ToUpperInvariant() ?? "";
        if (!locationId.HasValue && term.Length == 0) return new JsonResult(Array.Empty<object>());
        var products = locationId.HasValue ? service.ProductsAt(locationId.Value) : db.Products.AsNoTracking();
        return new JsonResult(await products.WhereProductText(term, p => p.Sku.ToUpper().Contains(term) ||
                (p.ExternalReference != null && p.ExternalReference.ToUpper().Contains(term)) || p.Barcodes.Any(b => b.IsActive && b.Barcode.ToUpper().Contains(term)))
            .OrderBy(p => p.Sku).Take(12).Select(p => new { p.Id, p.Sku, p.Description, p.IsActive }).ToListAsync(ct));
    }
    public async Task<IActionResult> OnGetLocationsAsync(Guid productId, string? q, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest();
        if (!await db.Products.AnyAsync(p => p.Id == productId, ct)) return NotFound();
        var term = q?.Trim().ToUpperInvariant() ?? "";
        return new JsonResult(await db.Locations.AsNoTracking().Where(l => l.OperationalRole != LocationOperationalRole.Wip &&
            (db.InventoryBalances.Any(b => b.ProductId == productId && b.LocationId == l.Id) ||
             db.ProductLocationAssignments.Any(a => a.ProductId == productId && a.LocationId == l.Id) ||
             db.InventoryMovementLines.Any(m => m.ProductId == productId && (m.SourceLocationId == l.Id || m.DestinationLocationId == l.Id))))
            .Where(l => l.Code.ToUpper().Contains(term) || l.Description != null && l.Description.ToUpper().Contains(term))
            .OrderBy(l => l.Code).Take(12).Select(l => new { l.Id, l.Code, l.Description, l.IsActive, l.IsBlocked }).ToListAsync(ct));
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var photos = await ReadPhotosAsync(ct);
        if (ModelState.IsValid)
        {
            var result = await service.ReportAsync(new(OperationId, RequestContext, ContextToken, Scope, Kind, Difference, Quantity, Description, Pin, photos), ct);
            if (result.Id.HasValue && result.Error is null) return RedirectToPage("Details", new { id = result.Id });
            ModelState.AddModelError("", result.Error ?? MaterialIncidentService.InvalidContext);
        }
        ClearCredentials();
        if (!await LoadAsync(ct)) return NotFound();
        ContextToken = Context?.Token ?? ""; ModelState.Remove(nameof(ContextToken));
        return Page();
    }
    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (!ProductId.HasValue && !LocationId.HasValue && !ArrivalLineId.HasValue && !PlateId.HasValue)
            return HttpMethods.IsGet(Request.Method);
        Context = await service.ContextAsync(RequestContext, ct);
        if (Context is null)
            return !ProductId.HasValue && !ArrivalLineId.HasValue && !PlateId.HasValue && LocationId.HasValue &&
                await db.Locations.AnyAsync(l => l.Id == LocationId && l.OperationalRole != LocationOperationalRole.Wip, ct);
        ProductId = Context.ProductId; LocationId = Context.LocationId;
        if (ArrivalLineId.HasValue) Plates = await db.PalletPlateEvents.Where(e => e.MovementLineId == ArrivalLineId).Select(e => e.PlateId).Distinct().ToListAsync(ct);
        if (PlateId.HasValue) Locations = await db.Locations.AsNoTracking().Where(l => l.OperationalRole != LocationOperationalRole.Wip).OrderBy(l => l.Code).ToListAsync(ct);
        return true;
    }
}
