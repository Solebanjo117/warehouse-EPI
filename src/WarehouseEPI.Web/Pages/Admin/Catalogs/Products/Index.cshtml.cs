using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(WarehouseDbContext dbContext) : PageModel
{
    private const int PageSize = 50;
    public IReadOnlyList<ProductRow> Products { get; private set; } = [];
    public string? Search { get; private set; }
    public string Status { get; private set; } = "active";
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; }

    public async Task OnGetAsync(string? search, string status = "active", int page = 1, CancellationToken token = default)
    {
        Search = search?.Trim(); Status = status is "all" or "inactive" ? status : "active"; CurrentPage = Math.Max(1, page);
        var query = dbContext.Products.AsNoTracking();
        if (Status == "active") query = query.Where(x => x.IsActive);
        else if (Status == "inactive") query = query.Where(x => !x.IsActive);
        if (!string.IsNullOrWhiteSpace(Search))
            query = ProductPageSupport.ApplySearch(query, Search);
        var count = await query.CountAsync(token); TotalPages = Math.Max(1, (int)Math.Ceiling(count / (double)PageSize)); CurrentPage = Math.Min(CurrentPage, TotalPages);
        Products = await query.OrderBy(x => x.Sku).Skip((CurrentPage - 1) * PageSize).Take(PageSize)
            .Select(x => new ProductRow(x.Id, x.Sku, x.Description, x.ExternalReference, x.BaseUnit.Code,
                x.ProductType != null ? x.ProductType.Code : null, x.ProductClass != null ? x.ProductClass.Code : null,
                x.Barcodes.Count(b => b.IsActive), x.IsActive)).ToListAsync(token);
    }

    public sealed record ProductRow(Guid Id,string Sku,string? Description,string? ExternalReference,string Unit,string? Type,string? Class,int BarcodeCount,bool IsActive);
}
