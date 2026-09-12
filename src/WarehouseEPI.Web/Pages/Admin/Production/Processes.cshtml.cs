using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Admin.Production;

[Authorize(Policy = "AdminOnly")]
public sealed class ProcessesModel(ProductionProcessConfigurationService processes) : PageModel
{
    public string? Search { get; set; }
    public IReadOnlyList<ProcessRow> Items { get; private set; } = [];
    public int PageNumber { get; private set; }
    public bool HasPrevious { get; private set; }
    public bool HasNext { get; private set; }
    public async Task OnGetAsync(string? search, int pageNumber = 1, CancellationToken token = default)
    {
        Search = search;
        var page = await processes.ListAsync(search, pageNumber, token);
        Items = page.Items; PageNumber = page.PageNumber; HasPrevious = page.HasPrevious; HasNext = page.HasNext;
    }
}
