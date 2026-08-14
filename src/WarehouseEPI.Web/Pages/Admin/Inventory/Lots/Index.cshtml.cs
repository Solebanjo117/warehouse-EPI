using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Lots;

public sealed class IndexModel(WarehouseDbContext db) : PageModel
{
    public IReadOnlyList<Row> Lots { get; private set; } = [];
    public async Task OnGetAsync(string? search, CancellationToken cancellationToken)
    {
        var term = search?.Trim().ToUpperInvariant();
        Lots = await (from item in db.ProductLots.AsNoTracking()
                      join balance in db.InventoryBalances.AsNoTracking() on item.Id equals balance.LotId into balances
                      where string.IsNullOrEmpty(term) || item.Product.Sku.Contains(term) || item.NormalizedNumber.Contains(term)
                      orderby item.Product.Sku, item.LotDate, item.Number
                      select new Row(item.Id, item.Product.Sku, item.Number, item.LotDate,
                          item.Product.BaseUnit.Code, balances.Sum(balance => (decimal?)balance.Quantity) ?? 0m))
            .Take(200).ToListAsync(cancellationToken);
    }
    public sealed record Row(Guid Id, string Sku, string Number, DateOnly? LotDate, string Unit, decimal Quantity);
}
