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

    public async Task OnGetAsync(string? search, string status = "active", int pageNumber = 1, CancellationToken token = default)
    {
        Search = search?.Trim(); Status = status is "all" or "inactive" ? status : "active"; CurrentPage = Math.Max(1, pageNumber);
        var query = dbContext.Products.AsNoTracking();
        if (Status == "active") query = query.Where(x => x.IsActive);
        else if (Status == "inactive") query = query.Where(x => !x.IsActive);
        if (!string.IsNullOrWhiteSpace(Search))
            query = ProductPageSupport.ApplySearch(query, Search);
        var count = await query.CountAsync(token); TotalPages = Math.Max(1, (int)Math.Ceiling(count / (double)PageSize)); CurrentPage = Math.Min(CurrentPage, TotalPages);
        var products = await query.OrderBy(x => x.Sku).Skip((CurrentPage - 1) * PageSize).Take(PageSize)
            .Select(x => new ProductBaseRow(x.Id, x.Sku, x.Description, x.ExternalReference, x.BaseUnit.Code,
                x.ProductType != null ? x.ProductType.Code : null, x.ProductClass != null ? x.ProductClass.Code : null,
                x.Barcodes.Count(b => b.IsActive), x.IsActive)).ToListAsync(token);
        var productIds = products.Select(product => product.Id).ToArray();
        var assignments = productIds.Length == 0 ? [] : await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(assignment => assignment.IsActive && productIds.Contains(assignment.ProductId))
            .OrderBy(assignment => assignment.Location.RowCode).ThenBy(assignment => assignment.Location.RackNumber)
            .ThenBy(assignment => assignment.Location.PalletNumber).ThenBy(assignment => assignment.Location.Code)
            .Select(assignment => new LocationAssignmentLink(assignment.ProductId, assignment.LocationId, assignment.Location.Code))
            .ToListAsync(token);
        var byProduct = assignments.GroupBy(assignment => assignment.ProductId).ToDictionary(group => group.Key, group => group.ToArray());
        Products = products.Select(product =>
        {
            var locations = byProduct.GetValueOrDefault(product.Id) ?? [];
            return new ProductRow(product.Id, product.Sku, product.Description, product.ExternalReference, product.Unit,
                product.Type, product.Class, product.BarcodeCount, product.IsActive,
                locations.Take(3).Select(location => new LocationLink(location.LocationId, location.Code)).ToArray(), locations.Length);
        }).ToArray();
    }

    public sealed record ProductRow(Guid Id, string Sku, string? Description, string? ExternalReference, string Unit, string? Type, string? Class, int BarcodeCount, bool IsActive, IReadOnlyList<LocationLink> Locations, int LocationCount);
    public sealed record LocationLink(Guid Id, string Code);
    private sealed record ProductBaseRow(Guid Id, string Sku, string? Description, string? ExternalReference, string Unit, string? Type, string? Class, int BarcodeCount, bool IsActive);
    private sealed record LocationAssignmentLink(Guid ProductId, Guid LocationId, string Code);
}
