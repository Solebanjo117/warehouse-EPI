using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.WipDocuments;

[Authorize(Policy = "AdminOnly")]
public sealed class ReverseModel(WarehouseDbContext db, WipDocumentService service) : PageModel
{
    [BindProperty] public Guid OriginalOperationId { get; set; }
    [BindProperty] public Guid OperationId { get; set; }
    [BindProperty, Required, StringLength(500)] public string Reason { get; set; } = "";
    [BindProperty, Required] public string Pin { get; set; } = "";
    public string Summary { get; private set; } = "";
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        OriginalOperationId = id; OperationId = Guid.NewGuid();
        return await LoadAsync(token) ? Page() : NotFound();
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        var pin = Pin; Pin = ""; ModelState.Remove(nameof(Pin));
        if (!await LoadAsync(token)) return NotFound();
        if (ModelState.IsValid)
        {
            var result = await service.ReverseAsync(OperationId, OriginalOperationId, pin, Reason, token);
            if (result.Status == InventoryMovementStatus.Success) return RedirectToPage("/Reports/Wip/Index");
            foreach (var error in result.ValidationErrors.DefaultIfEmpty("No fue posible validar el reverso o el NIP ADMIN.")) ModelState.AddModelError(string.Empty, error);
        }
        return Page();
    }
    private async Task<bool> LoadAsync(CancellationToken token)
    {
        var rows = await db.WipDocumentApplications.Where(x => x.OperationId == OriginalOperationId && x.IssueLinkId == null)
            .Select(x => new { x.Document.Product.Sku, x.Document.WipLocation.Code, x.Quantity, Unit = x.Document.Product.BaseUnit.Code, Kind = x.Kind })
            .ToListAsync(token);
        Summary = string.Join("; ", rows.Select(x => $"{x.Sku} · {x.Code} · {x.Quantity:0.####} {x.Unit} · {x.Kind}"));
        return rows.Count > 0;
    }
}
