using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class EditModel(
    WarehouseDbContext dbContext,
    ProductLocationAssignmentService assignmentService) : PageModel, IProductFormPage
{
    [BindProperty] public ProductInputModel Input { get; set; } = new();
    public IReadOnlyList<SelectListItem> Units { get; private set; } = []; public IReadOnlyList<SelectListItem> Types { get; private set; } = []; public IReadOnlyList<SelectListItem> Classes { get; private set; } = [];
    public IReadOnlyList<LocationAssignmentRow> LocationAssignments { get; private set; } = [];
    public IReadOnlyList<LocationSearchRow> LocationResults { get; private set; } = [];
    public string? LocationSearch { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, string? locationSearch, CancellationToken token)
    {
        LocationSearch = locationSearch?.Trim();
        var product = await dbContext.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token); if (product is null) return NotFound();
        Input = new ProductInputModel { Id = product.Id, Sku = product.Sku, Description = product.Description, ExternalReference = product.ExternalReference, ProductTypeId = product.ProductTypeId, ProductClassId = product.ProductClassId, BaseUnitId = product.BaseUnitId, MinimumStock = product.MinimumStock, IsActive = product.IsActive };
        await LoadAsync(token); return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        ProductPageSupport.Normalize(Input); await ProductPageSupport.ValidateAsync(dbContext, Input, ModelState, token);
        var product = await dbContext.Products.SingleOrDefaultAsync(x => x.Id == Input.Id, token); if (product is null) return NotFound();
        if (!ModelState.IsValid) { await LoadAsync(token); return Page(); }
        ProductPageSupport.Apply(product, Input);
        try { await dbContext.SaveChangesAsync(token); } catch (DbUpdateException) { ModelState.AddModelError("Input.Sku", "No fue posible guardar; verifique que el SKU no esté repetido."); await LoadAsync(token); return Page(); }
        TempData["Success"] = "Producto actualizado."; return RedirectToPage("Details", new { id = Input.Id });
    }

    public async Task<IActionResult> OnPostAssignLocationAsync(Guid id, Guid locationId, CancellationToken token)
    {
        var result = await assignmentService.AssignAsync(id, locationId, token);
        if (result == ProductLocationAssignmentResult.Success) TempData["Success"] = "Ubicación asignada al producto.";
        else TempData["Error"] = AssignmentError(result);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeactivateLocationAsync(Guid id, Guid locationId, CancellationToken token)
    {
        var result = await assignmentService.DeactivateAsync(id, locationId, token);
        if (result == ProductLocationAssignmentResult.Success) TempData["Success"] = "La asignación fue desactivada.";
        else TempData["Error"] = "La asignación activa ya no existe.";
        return RedirectToPage(new { id });
    }

    private async Task LoadAsync(CancellationToken token)
    {
        (Units, Types, Classes) = await ProductPageSupport.LoadOptionsAsync(dbContext, Input, token);
        LocationAssignments = await dbContext.ProductLocationAssignments.AsNoTracking().Where(x => x.ProductId == Input.Id)
            .OrderByDescending(x => x.IsActive).ThenBy(x => x.Location.RowCode).ThenBy(x => x.Location.RackNumber).ThenBy(x => x.Location.PalletNumber).ThenBy(x => x.Location.Code)
            .Select(x => new LocationAssignmentRow(x.LocationId, x.Location.Code, x.Location.Description, x.Location.IsActive, x.Location.IsBlocked, x.IsActive)).ToListAsync(token);
        if (!string.IsNullOrWhiteSpace(LocationSearch))
        {
            var term = LocationSearch.ToUpperInvariant();
            LocationResults = await dbContext.Locations.AsNoTracking().Where(x => x.IsActive && !x.IsBlocked &&
                    x.OperationalRole != LocationOperationalRole.Wip &&
                    (x.Code.Contains(term) || (x.Description != null && x.Description.ToUpper().Contains(term))))
                .OrderBy(x => x.RowCode).ThenBy(x => x.RackNumber).ThenBy(x => x.PalletNumber).ThenBy(x => x.Code).Take(20)
                .Select(x => new LocationSearchRow(x.Id, x.Code, x.Description,
                    x.ProductAssignments.Any(a => a.ProductId == Input.Id && a.IsActive))).ToListAsync(token);
        }
    }
    private static string AssignmentError(ProductLocationAssignmentResult result) => result switch
    {
        ProductLocationAssignmentResult.AlreadyActive => "El producto ya está asignado a esa ubicación.",
        ProductLocationAssignmentResult.ProductInactive => "No se puede asignar un producto inactivo.",
        ProductLocationAssignmentResult.LocationInactive => "No se puede asignar a una ubicación inactiva.",
        ProductLocationAssignmentResult.LocationBlocked => "No se puede asignar a una ubicación bloqueada.",
        ProductLocationAssignmentResult.LocationDoesNotTrackInventory => "Los racks WIP no admiten asignaciones de inventario.",
        _ => "El producto o la ubicación ya no existe."
    };
    public sealed record LocationAssignmentRow(Guid LocationId, string Code, string? Description, bool LocationIsActive, bool LocationIsBlocked, bool IsActive);
    public sealed record LocationSearchRow(Guid Id, string Code, string? Description, bool IsAssigned);
}
