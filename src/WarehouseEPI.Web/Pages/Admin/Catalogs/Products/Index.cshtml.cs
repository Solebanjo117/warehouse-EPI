using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using WarehouseEPI.Infrastructure.Catalogs;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(ProductCatalogQueryService catalog, WarehouseDbContext dbContext) : PageModel
{
    private const int PageSize = 25;
    public IReadOnlyList<ProductRow> Products { get; private set; } = [];
    public ProductCatalogSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public string? Search { get; private set; }
    public string Status { get; private set; } = "active";
    public string Stock { get; private set; } = "all";
    public string Assignment { get; private set; } = "all";
    public short? UnitId { get; private set; }
    public short? TypeId { get; private set; }
    public short? ClassId { get; private set; }
    public IReadOnlyList<SelectListItem> UnitOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> TypeOptions { get; private set; } = [];
    public IReadOnlyList<SelectListItem> ClassOptions { get; private set; } = [];
    public IReadOnlyList<int> VisiblePages { get; private set; } = [];
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; }
    public int TotalResults { get; private set; }
    public int FirstResult => TotalResults == 0 ? 0 : ((CurrentPage - 1) * PageSize) + 1;
    public int LastResult => Math.Min(CurrentPage * PageSize, TotalResults);
    public string? UnitLabel => LabelFor(UnitOptions, UnitId);
    public string? TypeLabel => LabelFor(TypeOptions, TypeId);
    public string? ClassLabel => LabelFor(ClassOptions, ClassId);
    public bool HasCatalogFilters => UnitId is not null || TypeId is not null || ClassId is not null;
    public int CatalogFilterCount => (UnitId is null ? 0 : 1) + (TypeId is null ? 0 : 1) + (ClassId is null ? 0 : 1);
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(Search) || Status != "active" || Stock != "all" || Assignment != "all" || HasCatalogFilters;
    public string? QuickFilter => (Status, Stock, Assignment) switch
    {
        ("active", "all", "all") => "active",
        ("inactive", "all", "all") => "inactive",
        ("active", "balance", "all") => "balance",
        ("active", "negative", "all") => "negative",
        ("active", "minimum", "all") => "minimum",
        ("active", "all", "unassigned") => "unassigned",
        _ => null
    };

    public async Task<IActionResult> OnGetAsync(string? search, string status = "active", string stock = "all", string assignment = "all", short? unitId = null, short? typeId = null, short? classId = null, int pageNumber = 1, CancellationToken token = default)
    {
        Search = search?.Trim();
        Status = status is "all" or "inactive" ? status : "active";
        Stock = stock is "balance" or "empty" or "negative" or "minimum" ? stock : "all";
        Assignment = assignment is "assigned" or "unassigned" ? assignment : "all";
        CurrentPage = Math.Max(1, pageNumber);

        (UnitOptions, TypeOptions, ClassOptions) = await ProductPageSupport.LoadFilterOptionsAsync(dbContext, unitId, typeId, classId, token);
        UnitId = Known(UnitOptions, unitId);
        TypeId = Known(TypeOptions, typeId);
        ClassId = Known(ClassOptions, classId);

        var result = await catalog.SearchAsync(new(Search, Status, Stock, Assignment, UnitId, TypeId, ClassId), CurrentPage, PageSize, token);
        Summary = result.Summary;
        TotalResults = result.TotalCount;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalResults / (double)PageSize));
        CurrentPage = Math.Min(CurrentPage, TotalPages);
        var firstVisible = Math.Max(1, CurrentPage - 2);
        var lastVisible = Math.Min(TotalPages, CurrentPage + 2);
        VisiblePages = Enumerable.Range(firstVisible, lastVisible - firstVisible + 1).ToArray();
        Products = result.Items.Select(p => new ProductRow(p.Id, p.Sku, p.Description, p.Reference, p.Unit, p.Type, p.Class, p.IsActive, p.Quantity, p.Minimum, p.Locations, p.IsNegative, p.IsBelowMinimum, p.HasAssignment)).ToArray();
        return Page();
    }

    private static short? Known(IReadOnlyList<SelectListItem> options, short? value) =>
        value is null || options.Any(option => option.Value == value.Value.ToString(CultureInfo.InvariantCulture)) ? value : null;

    private static string? LabelFor(IReadOnlyList<SelectListItem> options, short? value) =>
        value is null ? null : options.FirstOrDefault(option => option.Value == value.Value.ToString(CultureInfo.InvariantCulture))?.Text;

    public sealed record ProductRow(Guid Id, string Sku, string? Description, string? ExternalReference, string Unit, string? Type, string? Class, bool IsActive, decimal Quantity, decimal Minimum, int LocationCount, bool IsNegative, bool IsBelowMinimum, bool HasAssignment);
}
