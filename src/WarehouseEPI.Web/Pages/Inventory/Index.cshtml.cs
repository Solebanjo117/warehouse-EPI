using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Inventory;

public sealed class IndexModel(
    OperationalInventoryQueryService operationalQuery,
    InventoryQueryService inventoryQuery) : PageModel
{
    public OperationalProductResult? Product { get; private set; }
    public OperationalLocationResult? Location { get; private set; }
    public IReadOnlyList<InventoryBalanceView> Balances { get; private set; } = [];
    public decimal ProductTotal { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(
        Guid? productId,
        string? productCode,
        Guid? locationId,
        string? locationCode,
        CancellationToken cancellationToken)
    {
        if (productId is not null || !string.IsNullOrWhiteSpace(productCode))
        {
            Product = productId is Guid id
                ? await operationalQuery.GetProductAsync(id, false, cancellationToken)
                : await operationalQuery.ResolveProductAsync(productCode, false, cancellationToken);
            if (Product is null)
            {
                ErrorMessage = "No se encontró el producto.";
                return;
            }
            Balances = await inventoryQuery.GetProductBalancesAsync(Product.Id, cancellationToken);
            ProductTotal = await inventoryQuery.GetProductTotalAsync(Product.Id, cancellationToken);
            return;
        }

        if (locationId is not null || !string.IsNullOrWhiteSpace(locationCode))
        {
            Location = locationId is Guid id
                ? await operationalQuery.GetLocationAsync(id, false, cancellationToken)
                : await operationalQuery.ResolveLocationAsync(locationCode, false, cancellationToken);
            if (Location is null)
            {
                ErrorMessage = "No se encontró la ubicación.";
                return;
            }
            Balances = await inventoryQuery.GetLocationContentsAsync(Location.Id, cancellationToken);
        }
    }
}
