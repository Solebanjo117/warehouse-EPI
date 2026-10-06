using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Admin.Inventory;

[Authorize(Policy = "AdminOnly")]
public sealed class WipCutoverModel(WipDocumentCutoverService service) : PageModel
{
    public WipCutoverPreview Preview { get; private set; } = new("", [], [], false);
    [BindProperty] public Guid OperationId { get; set; }
    [BindProperty] public string Revision { get; set; } = "";
    [BindProperty, Required, StringLength(500)] public string Reason { get; set; } = "";
    [BindProperty, Required] public string Pin { get; set; } = "";
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken token)
    {
        OperationId = Guid.NewGuid();
        Preview = await service.PreviewAsync(token);
        Revision = Preview.Revision;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        var pin = Pin; Pin = ""; ModelState.Remove(nameof(Pin));
        if (string.IsNullOrWhiteSpace(pin)) ModelState.AddModelError(nameof(Pin), "Indica el NIP ADMIN.");
        if (ModelState.IsValid)
        {
            var result = await service.ConfirmAsync(new(OperationId, Revision, pin, Reason), token);
            if (result.Status == InventoryMovementStatus.Success)
            {
                Message = "Corte documental aplicado. Los saldos del almacén se conservaron.";
                return RedirectToPage();
            }
            foreach (var error in result.ValidationErrors.DefaultIfEmpty("No fue posible confirmar el corte. Verifica el NIP y la revisión."))
                ModelState.AddModelError(string.Empty, error);
        }
        Preview = await service.PreviewAsync(token);
        Revision = Preview.Revision; ModelState.Remove(nameof(Revision));
        return Page();
    }
}
