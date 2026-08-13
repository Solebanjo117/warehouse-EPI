using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Units;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(WarehouseDbContext dbContext) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<UnitRow> Items { get; private set; } = [];

    public async Task OnGetAsync(short? editId, CancellationToken cancellationToken)
    {
        if (editId.HasValue)
        {
            var unit = await dbContext.Units.AsNoTracking().SingleOrDefaultAsync(x => x.Id == editId, cancellationToken);
            if (unit is not null)
                Input = new InputModel { Id = unit.Id, Code = unit.Code, Name = unit.Name, AllowsDecimals = unit.AllowsDecimals };
        }
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        Input.Code = CatalogNormalization.NormalizeCode(Input.Code);
        Input.Name = Input.Name.Trim();
        if (!ModelState.IsValid) { await LoadAsync(cancellationToken); return Page(); }

        var duplicate = await dbContext.Units.AnyAsync(x => x.Code == Input.Code && x.Id != Input.Id, cancellationToken);
        if (duplicate) { ModelState.AddModelError("Input.Code", "Ya existe una unidad con ese código."); await LoadAsync(cancellationToken); return Page(); }

        if (Input.Id == 0)
            dbContext.Units.Add(new Unit { Code = Input.Code, Name = Input.Name, AllowsDecimals = Input.AllowsDecimals });
        else
        {
            var unit = await dbContext.Units.SingleOrDefaultAsync(x => x.Id == Input.Id, cancellationToken);
            if (unit is null) return NotFound();
            if (unit.Code == CatalogDefaults.UnassignedUnitCode)
            {
                TempData["Error"] = "La unidad Sin asignar está reservada para importaciones y no puede editarse.";
                return RedirectToPage();
            }
            unit.Code = Input.Code; unit.Name = Input.Name; unit.AllowsDecimals = Input.AllowsDecimals;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(short id, CancellationToken cancellationToken)
    {
        var unit = await dbContext.Units.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (unit is null) return NotFound();
        if (unit.Code == CatalogDefaults.UnassignedUnitCode)
        {
            TempData["Error"] = "La unidad Sin asignar está reservada para importaciones y debe permanecer activa.";
            return RedirectToPage();
        }
        if (unit.IsActive)
        {
            var count = await dbContext.Products.CountAsync(x => x.IsActive && x.BaseUnitId == id, cancellationToken);
            if (count > 0) { TempData["Error"] = $"No se puede desactivar: {count} producto(s) activo(s) usan esta unidad."; return RedirectToPage(); }
        }
        unit.IsActive = !unit.IsActive;
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken token) => Items = await dbContext.Units.AsNoTracking()
        .OrderBy(x => x.Code).Select(x => new UnitRow(x.Id, x.Code, x.Name, x.AllowsDecimals, x.IsActive,
            x.Code == CatalogDefaults.UnassignedUnitCode)).ToListAsync(token);

    public sealed class InputModel
    {
        public short Id { get; set; }
        [Required, StringLength(20)] public string Code { get; set; } = string.Empty;
        [Required, StringLength(80)] public string Name { get; set; } = string.Empty;
        public bool AllowsDecimals { get; set; } = true;
    }
    public sealed record UnitRow(short Id, string Code, string Name, bool AllowsDecimals, bool IsActive, bool IsSystem);
}
