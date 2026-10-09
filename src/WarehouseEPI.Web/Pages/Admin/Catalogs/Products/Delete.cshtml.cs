using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Catalogs;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class DeleteModel(WarehouseDbContext db, IStringLocalizer<CatalogTexts> texts) : PageModel
{
    public Product Product { get; private set; } = null!;
    public bool CanDelete { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        if (product is null) return NotFound();
        Product = product;
        CanDelete = !await new ProductDeletionService(db).HasUsageAsync(id, token);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken token)
    {
        var result = await new ProductDeletionService(db).DeleteAsync(id, token);
        if (result == ProductDeletionResult.NotFound) return NotFound();
        if (result == ProductDeletionResult.Deleted)
        {
            TempData["Success"] = texts["Producto eliminado."].Value;
            return RedirectToPage("Index");
        }
        TempData["Error"] = texts[result == ProductDeletionResult.InUse
            ? "No se puede eliminar: el producto tiene existencias, historial o dependencias. Puedes desactivarlo desde Editar producto."
            : "El producto cambió mientras intentabas eliminarlo. Vuelve a revisar su estado."].Value;
        return RedirectToPage(new { id });
    }
}
