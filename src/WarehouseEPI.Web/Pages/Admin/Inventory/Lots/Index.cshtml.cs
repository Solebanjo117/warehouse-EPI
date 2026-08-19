using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Lots;

public sealed class IndexModel(ProductLotQueryService lots) : PageModel
{
    private const int PageSize = 25;
    public ProductLotPage Results { get; private set; } = new([], 0);
    public int PageNumber { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Results.TotalCount / (double)PageSize));
    public string? Search { get; private set; }
    public LotBalanceFilter Balance { get; private set; }
    public LotDateFilter Date { get; private set; }
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }

    public async Task OnGetAsync(string? search, LotBalanceFilter balance = LotBalanceFilter.All, LotDateFilter date = LotDateFilter.All, DateOnly? from = null, DateOnly? to = null, int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        Search = search?.Trim(); Balance = balance; Date = date; From = from; To = to; PageNumber = Math.Max(1, pageNumber);
        Results = await lots.SearchAsync(new(Search, Balance, Date, From, To), PageNumber, PageSize, cancellationToken);
        PageNumber = Math.Min(PageNumber, TotalPages);
    }
}
