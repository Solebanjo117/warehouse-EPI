using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class LookupModel(
    OperationalInventoryQueryService operationalQuery,
    InventoryQueryService inventoryQuery) : PageModel
{
    public async Task<IActionResult> OnGetProductsAsync(string? q, CancellationToken cancellationToken) =>
        new JsonResult(await operationalQuery.SearchProductsAsync(q, cancellationToken));

    public async Task<IActionResult> OnGetResolveProductAsync(string? code, CancellationToken cancellationToken)
    {
        var result = await operationalQuery.ResolveProductAsync(code, cancellationToken: cancellationToken);
        return result is null ? NotFound() : new JsonResult(result);
    }

    public async Task<IActionResult> OnGetLocationsAsync(string? q, CancellationToken cancellationToken) =>
        new JsonResult(await operationalQuery.SearchLocationsAsync(q, cancellationToken));

    public async Task<IActionResult> OnGetResolveLocationAsync(string? code, CancellationToken cancellationToken)
    {
        var result = await operationalQuery.ResolveLocationAsync(code, cancellationToken: cancellationToken);
        return result is null ? NotFound() : new JsonResult(result);
    }

    public async Task<IActionResult> OnGetResolveCodeAsync(string? code, CancellationToken cancellationToken) =>
        new JsonResult(await operationalQuery.ResolveCodeAsync(code, cancellationToken));

    public async Task<IActionResult> OnGetProductLocationsAsync(Guid productId, CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty)
            return BadRequest();
        return new JsonResult(await operationalQuery.GetProductLocationsAsync(productId, cancellationToken));
    }

    public async Task<IActionResult> OnGetLocationProductsAsync(Guid locationId, CancellationToken cancellationToken)
    {
        if (locationId == Guid.Empty)
            return BadRequest();
        return new JsonResult(await operationalQuery.GetLocationProductsAsync(locationId, cancellationToken));
    }

    public async Task<IActionResult> OnGetBalanceAsync(Guid productId, Guid locationId, CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty || locationId == Guid.Empty)
            return BadRequest();
        return new JsonResult(await inventoryQuery.GetBalanceAsync(productId, locationId, cancellationToken));
    }
}
