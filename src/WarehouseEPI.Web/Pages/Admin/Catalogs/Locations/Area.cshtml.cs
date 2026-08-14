using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class AreaModel(WarehouseDbContext dbContext) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public bool IsEdit => Input.Id != Guid.Empty;

    public async Task<IActionResult> OnGetAsync(Guid? locationId, CancellationToken cancellationToken)
    {
        if (locationId is null) return Page();
        var location = await dbContext.Locations.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == locationId, cancellationToken);
        if (location is null) return NotFound();
        if (location.Kind != LocationKind.Area) return BadRequest();
        Input = new() { Id = location.Id, Code = location.Code, Description = location.Description };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Input.Code = LocationNormalization.NormalizeCode(Input.Code);
        Input.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        if (!LocationNormalization.IsValidAreaCode(Input.Code))
            ModelState.AddModelError("Input.Code", "Usa letras, números y guiones, sin espacios externos ni guiones al inicio o final.");
        if (!ModelState.IsValid) return Page();
        if (await dbContext.Locations.AnyAsync(location => location.Code == Input.Code && location.Id != Input.Id, cancellationToken))
        { ModelState.AddModelError("Input.Code", "Ya existe una ubicación con ese código."); return Page(); }
        if (Input.Id == Guid.Empty)
            dbContext.Locations.Add(new Location { Code = Input.Code, Kind = LocationKind.Area, Description = Input.Description });
        else
        {
            var location = await dbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == Input.Id, cancellationToken);
            if (location is null) return NotFound();
            if (location.Kind != LocationKind.Area) return BadRequest();
            location.Code = Input.Code; location.Description = Input.Description; location.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToPage("Index");
    }

    public sealed class InputModel
    {
        public Guid Id { get; set; }
        [Required, StringLength(40)] public string Code { get; set; } = string.Empty;
        [StringLength(200)] public string? Description { get; set; }
    }
}
