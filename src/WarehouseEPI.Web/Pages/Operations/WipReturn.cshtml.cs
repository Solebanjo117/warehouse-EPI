using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class WipReturnModel(
    WipDispositionService dispositionService,
    WipReportService reportService,
    OperationalInventoryQueryService operationalQuery) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<WipIssueRow> Issues { get; private set; } = [];
    public WipIssueRow? SelectedIssue { get; private set; }
    public IReadOnlyList<SharedLocationConflict> SharingConflicts { get; private set; } = [];
    public string? Search { get; private set; }

    public async Task OnGetAsync(string? search, Guid? lineId, CancellationToken cancellationToken)
    {
        Search = search?.Trim();
        Issues = await reportService.SearchIssuesAsync(Search, cancellationToken: cancellationToken);
        Input.OperationId = Guid.NewGuid();
        if (lineId is Guid selected)
        {
            Input.OriginalMovementLineId = selected;
            SelectedIssue = await reportService.GetIssueAsync(selected, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        SelectedIssue = Input.OriginalMovementLineId == Guid.Empty ? null :
            await reportService.GetIssueAsync(Input.OriginalMovementLineId, cancellationToken);
        if (SelectedIssue is null)
            ModelState.AddModelError("Input.OriginalMovementLineId", "Selecciona una salida WIP vigente.");

        Guid? destinationId = null;
        if (Input.Type == WipDispositionType.WarehouseReturn)
        {
            var destination = await operationalQuery.ResolveLocationAsync(Input.DestinationCode,
                cancellationToken: cancellationToken);
            if (destination is null || !destination.TracksInventory)
                ModelState.AddModelError("Input.DestinationCode", "La ubicación destino no existe o no controla saldo.");
            else
                destinationId = destination.Id;
        }
        if (!ModelState.IsValid)
        {
            Input.Pin = string.Empty;
            Issues = await reportService.SearchIssuesAsync(Search, cancellationToken: cancellationToken);
            return Page();
        }

        var result = await dispositionService.ConfirmAsync(new(
            Input.OperationId,
            Input.OriginalMovementLineId,
            Input.Type,
            Input.Quantity,
            Input.Pin,
            destinationId,
            Input.Reference,
            Input.Notes,
            Input.ApprovedSharedLocationIds.Select(id =>
                new SharedAssignmentApproval(SelectedIssue!.ProductId, id)).ToArray()), cancellationToken);
        Input.Pin = string.Empty;
        if (result.Status == WipDispositionStatus.Success && result.DispositionId is Guid dispositionId)
            return result.InventoryMovementId is Guid movementId
                ? RedirectToPage("/Operations/Receipt", new { id = movementId })
                : RedirectToPage("/Operations/WipReturnReceipt", new { id = dispositionId });
        if (result.Status == WipDispositionStatus.InvalidPin)
            ModelState.AddModelError(string.Empty, "No fue posible validar el NIP o el usuario.");
        else if (result.Status == WipDispositionStatus.RequiresLocationSharingConfirmation)
            SharingConflicts = result.Conflicts;
        else
            foreach (var error in result.ValidationErrors.DefaultIfEmpty("No fue posible confirmar la devolución."))
                ModelState.AddModelError(string.Empty, error);
        Issues = await reportService.SearchIssuesAsync(Search, cancellationToken: cancellationToken);
        return Page();
    }

    public sealed class InputModel
    {
        public Guid OperationId { get; set; }
        public Guid OriginalMovementLineId { get; set; }
        public WipDispositionType Type { get; set; }
        public decimal Quantity { get; set; }
        public string? DestinationCode { get; set; }
        [StringLength(120)] public string? Reference { get; set; }
        [StringLength(500)] public string? Notes { get; set; }
        public string Pin { get; set; } = string.Empty;
        public List<Guid> ApprovedSharedLocationIds { get; set; } = [];
    }
}
