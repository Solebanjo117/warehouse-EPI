using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.ProductTypes;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(WarehouseDbContext dbContext) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public IReadOnlyList<Row> Items { get; private set; } = [];
    public async Task OnGetAsync(short? editId, CancellationToken token) { if (editId.HasValue) { var x = await dbContext.ProductTypes.AsNoTracking().SingleOrDefaultAsync(y => y.Id == editId, token); if (x is not null) Input = new() { Id = x.Id, Code = x.Code, Name = x.Name }; } await LoadAsync(token); }
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken token)
    {
        Input.Code = CatalogNormalization.NormalizeCode(Input.Code); Input.Name = Input.Name.Trim();
        if (!ModelState.IsValid) { await LoadAsync(token); return Page(); }
        if (await dbContext.ProductTypes.AnyAsync(x => x.Code == Input.Code && x.Id != Input.Id, token)) { ModelState.AddModelError("Input.Code", "Ya existe un tipo con ese código."); await LoadAsync(token); return Page(); }
        if (Input.Id == 0) dbContext.ProductTypes.Add(new ProductType { Code = Input.Code, Name = Input.Name }); else { var x = await dbContext.ProductTypes.SingleOrDefaultAsync(y => y.Id == Input.Id, token); if (x is null) return NotFound(); x.Code = Input.Code; x.Name = Input.Name; }
        await dbContext.SaveChangesAsync(token); return RedirectToPage();
    }
    public async Task<IActionResult> OnPostToggleAsync(short id, CancellationToken token) { var x = await dbContext.ProductTypes.SingleOrDefaultAsync(y => y.Id == id, token); if (x is null) return NotFound(); if (x.IsActive) { var count = await dbContext.Products.CountAsync(p => p.IsActive && p.ProductTypeId == id, token); if (count > 0) { TempData["Error"] = $"No se puede desactivar: {count} producto(s) activo(s) usan este tipo."; return RedirectToPage(); } } x.IsActive = !x.IsActive; await dbContext.SaveChangesAsync(token); return RedirectToPage(); }
    private async Task LoadAsync(CancellationToken token) => Items = await dbContext.ProductTypes.AsNoTracking().OrderBy(x => x.Code).Select(x => new Row(x.Id, x.Code, x.Name, x.IsActive)).ToListAsync(token);
    public sealed class InputModel { public short Id { get; set; } [Required, StringLength(40)] public string Code { get; set; } = string.Empty; [Required, StringLength(100)] public string Name { get; set; } = string.Empty; }
    public sealed record Row(short Id, string Code, string Name, bool IsActive);
}
