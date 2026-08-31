using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations.Receiving;

public sealed class IndexModel(ReceivingQueryService query, WarehouseClock clock) : PageModel
{
    public ReceivingDocumentPage Results { get; private set; } = new([], 0);
    public Dictionary<Guid, DateTimeOffset> LocalDates { get; } = [];
    public string? Search { get; private set; }
    public ReceivingDocumentStatus? Status { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public const int PageSize = 25;

    public async Task OnGetAsync(string? search, ReceivingDocumentStatus? status, int pageNumber = 1, CancellationToken token = default)
    {
        Search = search?.Trim(); Status = status; PageNumber = Math.Max(1, pageNumber);
        Results = await query.SearchAsync(new(Search, Status, PageNumber, PageSize), token);
        foreach (var item in Results.Items) LocalDates[item.Id] = await clock.ConvertAsync(item.OpenedAt, token);
    }
}
