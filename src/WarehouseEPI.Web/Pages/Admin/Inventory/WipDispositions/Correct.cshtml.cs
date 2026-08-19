using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.WipDispositions;

[Authorize(Policy = "AdminOnly")]
public sealed class CorrectModel(WarehouseDbContext dbContext, WipDispositionCorrectionService correctionService) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public SummaryRow Summary { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        if (!await LoadAsync(id, token)) return NotFound();
        Input.OperationId = Guid.NewGuid(); Input.DispositionId = id;
        return Page();
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        if (!await LoadAsync(Input.DispositionId, token)) return NotFound();
        if (!ModelState.IsValid) { Input.Pin = string.Empty; return Page(); }
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var requester)) return Forbid();
        var result = await correctionService.ReverseAsync(new(Input.OperationId, Input.DispositionId,
            requester, Input.Pin, Input.Reason), token);
        Input.Pin = string.Empty;
        if (result.Status == WipDispositionStatus.Success)
            return RedirectToPage("/Reports/Wip/Details", new { id = Summary.OriginalLineId });
        foreach (var error in result.ValidationErrors.DefaultIfEmpty(result.Status == WipDispositionStatus.InvalidPin
                     ? "NIP ADMIN inválido." : "No fue posible corregir la devolución."))
            ModelState.AddModelError(string.Empty, error);
        return Page();
    }
    private async Task<bool> LoadAsync(Guid id, CancellationToken token)
    {
        var row = await dbContext.WipDispositions.AsNoTracking().Where(item => item.Id == id)
            .Select(item => new SummaryRow(item.Id, item.OriginalMovementLineId, item.OriginalMovementLine.Product.Sku,
                item.Type.ToString(), item.Quantity, item.OriginalMovementLine.Unit.Code,
                item.OriginalMovementLine.Movement.OperationalArea!.Code,
                item.ReversesDispositionId != null || dbContext.WipDispositions.Any(reverse => reverse.ReversesDispositionId == item.Id)))
            .SingleOrDefaultAsync(token);
        if (row is null) return false; Summary = row; return true;
    }
    public sealed class InputModel
    {
        public Guid OperationId { get; set; }
        public Guid DispositionId { get; set; }
        [Required, StringLength(500, MinimumLength = 3)] public string Reason { get; set; } = string.Empty;
        [Required] public string Pin { get; set; } = string.Empty;
    }
    public sealed record SummaryRow(Guid Id, Guid OriginalLineId, string Sku, string Type, decimal Quantity,
        string Unit, string Wip, bool IsCorrected);
}
