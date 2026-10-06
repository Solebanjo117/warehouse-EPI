using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed class AdvancedModel(ProductionQueryService query) : PageModel
{
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "active";
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public ProductionWorkOrderStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? ActiveStageId { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public bool AlertsOnly { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1; public ProductionOrderPage Result { get; private set; } = new([], 1, 25, 0);
    public IReadOnlyList<ProductionStage> Stages { get; private set; } = [];
    public ProductionQueueSnapshot Snapshot { get; private set; } = new("", DateTimeOffset.MinValue);
    private ProductionOrderSearch Filter() => new(Search, Status, ActiveStageId, From, To, AlertsOnly, PageNumber, View: View);
    public async Task<IActionResult> OnGetSnapshotAsync(CancellationToken t) => new JsonResult(await query.GetSnapshotAsync(Filter(), t));
    public async Task OnGetAsync(CancellationToken t) { Stages = await query.GetStagesAsync(t); Result = await query.SearchOrdersAsync(Filter(), t); Snapshot = await query.GetSnapshotAsync(Filter(), t); }
}
