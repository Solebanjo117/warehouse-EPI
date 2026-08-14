using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Lots;

public sealed class EditModel(WarehouseDbContext db, ProductLotAdministrationService lots) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string Title { get; private set; } = string.Empty;
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var lot = await db.ProductLots.AsNoTracking().Include(item => item.Product).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (lot is null) return NotFound();
        Title = $"{lot.Product.Sku} · {lot.Number}"; Input = new() { OperationId = Guid.NewGuid(), LotId = lot.Id, LotDate = lot.LotDate }; return Page();
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
        var result = await lots.ChangeDateAsync(new(Input.OperationId, Input.LotId, userId, Input.Pin, Input.LotDate, Input.Reason ?? string.Empty), cancellationToken);
        Input.Pin = string.Empty; ModelState.Remove("Input.Pin");
        if (result.Status == ProductLotDateChangeStatus.Success) return RedirectToPage("Index");
        foreach (var error in result.ValidationErrors.DefaultIfEmpty(result.Status == ProductLotDateChangeStatus.InvalidPin ? "NIP inválido o sin permiso administrativo." : "No fue posible actualizar la fecha.")) ModelState.AddModelError(string.Empty, error);
        Title = "Editar fecha de lote"; return Page();
    }
    public sealed class InputModel { public Guid OperationId { get; set; } public Guid LotId { get; set; } public DateOnly? LotDate { get; set; } public string? Reason { get; set; } public string Pin { get; set; } = string.Empty; }
}
