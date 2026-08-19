using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Inventory;

public sealed class IndexModel(
    OperationalInventoryQueryService operationalQuery,
    InventoryQueryService inventoryQuery) : PageModel
{
    private const int PageSize = 25;

    public OperationalProductResult? Product { get; private set; }
    public OperationalLocationResult? Location { get; private set; }
    public InventoryPositionPage Results { get; private set; } = new([], 0, 1, PageSize, new(0, 0, 0, 0, 0));
    public decimal ProductTotal { get; private set; }
    public string? ErrorMessage { get; private set; }
    public InventoryPositionFilter Filter { get; private set; }
    public Guid? HighlightLocationId { get; private set; }
    public Guid? HighlightProductId { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Results.TotalCount / (double)PageSize));

    public async Task OnGetAsync(
        Guid? productId,
        string? productCode,
        Guid? locationId,
        string? locationCode,
        string? code,
        string? filter,
        int pageNumber = 1,
        Guid? highlightLocationId = null,
        Guid? highlightProductId = null,
        CancellationToken cancellationToken = default)
    {
        Filter = ParseFilter(filter);
        HighlightLocationId = highlightLocationId;
        HighlightProductId = highlightProductId;

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
            await LoadProductAsync(pageNumber, cancellationToken);
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
            Results = await inventoryQuery.GetLocationInventoryPageAsync(Location.Id, Filter, pageNumber, PageSize, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(code))
            return;

        var resolution = await operationalQuery.ResolveInventoryCodeAsync(code, cancellationToken);
        if (resolution.Product is not null && resolution.Location is not null)
        {
            ErrorMessage = "El código coincide con un producto y una ubicación. Elige una sugerencia para consultar.";
            return;
        }
        if (resolution.Product is not null)
        {
            Product = resolution.Product;
            await LoadProductAsync(pageNumber, cancellationToken);
            return;
        }
        if (resolution.Location is not null)
        {
            Location = resolution.Location;
            Results = await inventoryQuery.GetLocationInventoryPageAsync(Location.Id, Filter, pageNumber, PageSize, cancellationToken);
            return;
        }
        ErrorMessage = "No se encontró un producto ni una ubicación con ese código.";
    }

    private async Task LoadProductAsync(int pageNumber, CancellationToken cancellationToken)
    {
        Results = await inventoryQuery.GetProductInventoryPageAsync(Product!.Id, Filter, pageNumber, PageSize, cancellationToken);
        ProductTotal = await inventoryQuery.GetProductTotalAsync(Product.Id, cancellationToken);
    }

    private static InventoryPositionFilter ParseFilter(string? value) => value?.ToLowerInvariant() switch
    {
        "balance" => InventoryPositionFilter.WithBalance,
        "negative" => InventoryPositionFilter.Negative,
        "assigned-zero" => InventoryPositionFilter.AssignedZero,
        "unassigned-balance" => InventoryPositionFilter.UnassignedBalance,
        _ => InventoryPositionFilter.All
    };
}
