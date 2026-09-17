using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Admin.Production;

[Authorize(Policy = "AdminOnly")]
public sealed class ReasonsModel(WarehouseDbContext db, ProductionExecutionService service) : PageModel
{
    public IReadOnlyList<ProductionReason> Reasons { get; private set; } = [];
    [BindProperty] public Guid OperationId { get; set; } = Guid.NewGuid();
    [BindProperty] public Guid ReasonId { get; set; } = Guid.NewGuid();
    [BindProperty] public uint Version { get; set; }
    [BindProperty] public ProductionReasonCategory Category { get; set; }
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string Description { get; set; } = "";
    [BindProperty] public bool IsActive { get; set; } = true;
    [BindProperty] public bool RequiresComment { get; set; }
    [BindProperty] public string Pin { get; set; } = "";
    public async Task OnGetAsync(Guid? id, CancellationToken token)
    {
        await Load(token);
        if (Reasons.SingleOrDefault(x => x.Id == id) is { } reason)
        { ReasonId = reason.Id; Version = reason.Version; Category = reason.Category; Code = reason.Code; Description = reason.Description; IsActive = reason.IsActive; RequiresComment = reason.RequiresComment; }
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        var result = await service.SaveReasonAsync(OperationId, ReasonId, Version, Category, Code ?? "", Description ?? "", IsActive, RequiresComment, Pin, token);
        Pin = ""; ModelState.Remove(nameof(Pin));
        if (result.Status == ProductionCommandStatus.Success) { TempData["Success"] = "Motivo guardado con historial."; return RedirectToPage(); }
        ModelState.AddModelError("", result.ValidationErrors.FirstOrDefault() ?? "NIP ADMIN inválido o el motivo cambió. Recarga antes de intentar nuevamente.");
        await Load(token); return Page();
    }
    private async Task Load(CancellationToken token) => Reasons = await db.ProductionReasons.AsNoTracking().OrderBy(x => x.Category).ThenBy(x => x.Code).ToListAsync(token);
    public static string Label(ProductionReasonCategory category) => category switch
    { ProductionReasonCategory.Scrap => "Merma", ProductionReasonCategory.Rework => "Retrabajo", ProductionReasonCategory.Difference => "Diferencia", _ => "Ajuste" };
}
