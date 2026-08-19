using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Catalogs;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(ProductCatalogQueryService catalog) : PageModel
{
    private const int PageSize = 25;
    public IReadOnlyList<ProductRow> Products { get; private set; } = [];
    public ProductCatalogSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public string? Search { get; private set; }
    public string Status { get; private set; } = "active";
    public string Stock { get; private set; } = "all";
    public string Assignment { get; private set; } = "all";
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? search, string status = "active", string stock = "all", string assignment = "all", short? unitId = null, short? typeId = null, short? classId = null, int pageNumber = 1, CancellationToken token = default)
    {
        Search = search?.Trim(); Status = status is "all" or "inactive" ? status : "active"; Stock = stock is "balance" or "empty" or "negative" or "minimum" ? stock : "all"; Assignment = assignment is "assigned" or "unassigned" ? assignment : "all"; CurrentPage = Math.Max(1, pageNumber);
        var result = await catalog.SearchAsync(new(Search, Status, Stock, Assignment, unitId, typeId, classId), CurrentPage, PageSize, token);
        Summary = result.Summary; TotalPages = Math.Max(1, (int)Math.Ceiling(result.TotalCount / (double)PageSize)); CurrentPage = Math.Min(CurrentPage, TotalPages);
        Products = result.Items.Select(p => new ProductRow(p.Id, p.Sku, p.Description, p.Reference, p.Unit, p.Type, p.Class, p.BarcodeCount, p.IsActive, p.Quantity, p.Minimum, p.Locations, p.IsNegative, p.IsBelowMinimum, p.HasAssignment)).ToArray();
        return Page();
    }

    public sealed record ProductRow(Guid Id, string Sku, string? Description, string? ExternalReference, string Unit, string? Type, string? Class, int BarcodeCount, bool IsActive, decimal Quantity, decimal Minimum, int LocationCount, bool IsNegative, bool IsBelowMinimum, bool HasAssignment);
}
