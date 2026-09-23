using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed class TraceModel(ProductionTraceabilityService traceability) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    public IReadOnlyList<ProductionTraceLinkView> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken token) =>
        Items = await traceability.SearchTraceAsync(Search, token);
}
