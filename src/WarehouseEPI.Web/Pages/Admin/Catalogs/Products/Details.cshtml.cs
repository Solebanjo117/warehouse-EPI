using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Catalogs;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class DetailsModel(ProductCatalogQueryService catalog) : PageModel
{
    public ProductCatalogDetail Product { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        var product = await catalog.GetAsync(id, token);
        if (product is null) return NotFound();
        Product = product;
        return Page();
    }
}
