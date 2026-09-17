using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations.ProductionSupply;

public sealed class IndexModel(ProductionSupplyService supplies) : PageModel
{
    public IReadOnlyList<ProductionSupplyQueueRow> Rows { get; private set; } = [];
    [BindProperty(SupportsGet=true)]public int PageNumber {get;set;}=1;
    public ProductionSupplyQueuePage QueuePage {get;private set;}=new([],1,0,1,"",DateTimeOffset.MinValue);
    public int PendingOrders { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Condition { get; set; }

    public async Task OnGetAsync(CancellationToken token)
    {
        QueuePage = await supplies.GetQueuePageAsync(Search,Condition,PageNumber,token);
        PageNumber=QueuePage.Page;Rows=QueuePage.Rows;PendingOrders=QueuePage.TotalOrders;
    }

    public async Task<IActionResult> OnGetSnapshotAsync(CancellationToken token) {return new JsonResult(await supplies.GetQueueSnapshotAsync(Search,Condition,token));}

    public async Task<IActionResult> OnPostStartAsync(Guid requestId, uint expectedVersion, string pin, CancellationToken token) =>
        Handle(await supplies.StartPreparationAsync(new(Guid.NewGuid(), requestId, expectedVersion, pin), token));

    public async Task<IActionResult> OnPostReserveAsync(Guid lineId, uint expectedVersion, decimal quantity, string pin, CancellationToken token) =>
        Handle(await supplies.ReserveAvailableAsync(new(Guid.NewGuid(), lineId, expectedVersion, quantity, pin), token));

    public async Task<IActionResult> OnPostProblemAsync(Guid requestId, uint expectedVersion, string pin, string reason, CancellationToken token) =>
        Handle(await supplies.ReportProblemAsync(new(Guid.NewGuid(), requestId, expectedVersion, pin, reason), token));

    public async Task<IActionResult> OnPostCancelAsync(Guid lineId, uint expectedVersion, decimal quantity, string pin, string reason, CancellationToken token) =>
        Handle(await supplies.CancelAsync(new(Guid.NewGuid(), lineId, expectedVersion, quantity, pin, reason), token));

    public async Task<IActionResult> OnPostPriorityAsync(Guid requestId, uint expectedVersion, ProductionSupplyPriority priority, string pin, string reason, CancellationToken token) =>
        Handle(await supplies.ChangePriorityAsync(new(Guid.NewGuid(), requestId, expectedVersion, priority, pin, reason), token));

    private IActionResult Handle(ProductionSupplyCommandResult result)
    {
        if (result.Status == ProductionSupplyCommandStatus.Success) TempData["SuccessMessage"] = "La solicitud se actualizó correctamente.";
        else TempData["ErrorMessage"] = result.Status switch
        {
            ProductionSupplyCommandStatus.InvalidPin => "No fue posible validar el NIP o el usuario.",
            ProductionSupplyCommandStatus.ConcurrencyConflict => "La solicitud cambió. Revisa sus cantidades e intenta nuevamente.",
            ProductionSupplyCommandStatus.IdempotencyConflict => "La operación ya se utilizó con otro contenido.",
            ProductionSupplyCommandStatus.NotFound => "La solicitud ya no está disponible.",
            _ => string.Join(" ", result.ValidationErrors)
        };
        return RedirectToPage(new { Search, Condition, PageNumber });
    }
}
