using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Catalogs;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class DetailsModel(ProductCatalogQueryService catalog, ProductionTraceabilityService production,
    ProductionWipDefaultService wipDefaults) : PageModel
{
    public ProductCatalogDetail Product { get; private set; } = null!;
    public ProductionProductConfigurationView Production { get; private set; } = null!;
    public MaterialWipDefaultsView WipConfiguration { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        var product = await catalog.GetAsync(id, token);
        if (product is null) return NotFound();
        Product = product;
        Production = await production.GetProductConfigurationAsync(id, token)
            ?? throw new InvalidOperationException("No se encontró la configuración del producto.");
        WipConfiguration = await wipDefaults.GetMaterialAsync(id, token)
            ?? throw new InvalidOperationException("No se encontró la configuración WIP del producto.");
        return Page();
    }
}
