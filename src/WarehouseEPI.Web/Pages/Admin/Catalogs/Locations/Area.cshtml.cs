using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class AreaModel(WarehouseDbContext dbContext, LocationAreaAdministrationService areas) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty] public DeleteInputModel DeleteInput { get; set; } = new();
    public LocationAreaDeletionState? Deletion { get; private set; }
    public IReadOnlyList<string> DeleteErrors { get; private set; } = [];
    public bool IsEdit => Input.Id != Guid.Empty;

    public async Task<IActionResult> OnGetAsync(Guid? locationId, CancellationToken cancellationToken)
    {
        if (locationId is null) return Page();
        var location = await dbContext.Locations.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == locationId, cancellationToken);
        if (location is null) return NotFound();
        if (location.Kind != LocationKind.Area) return BadRequest();
        Input = new() { Id = location.Id, Code = location.Code, Description = location.Description, OperationalRole = location.OperationalRole };
        await PrepareDeletionAsync(location.Id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Input.Code = LocationNormalization.NormalizeCode(Input.Code);
        Input.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        if (!LocationNormalization.IsValidAreaCode(Input.Code))
            ModelState.AddModelError("Input.Code", "Usa letras, números y guiones, sin espacios externos ni guiones al inicio o final.");
        if (!ModelState.IsValid)
        {
            await PrepareDeletionAsync(Input.Id, cancellationToken);
            return Page();
        }
        if (await dbContext.Locations.AnyAsync(location => location.Code == Input.Code && location.Id != Input.Id, cancellationToken))
        {
            ModelState.AddModelError("Input.Code", "Ya existe una ubicación con ese código.");
            await PrepareDeletionAsync(Input.Id, cancellationToken);
            return Page();
        }
        if (Input.Id == Guid.Empty)
            dbContext.Locations.Add(new Location { Code = Input.Code, Kind = LocationKind.Area, Description = Input.Description, OperationalRole = Input.OperationalRole });
        else
        {
            var location = await dbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == Input.Id, cancellationToken);
            if (location is null) return NotFound();
            if (location.Kind != LocationKind.Area) return BadRequest();
            location.Code = Input.Code; location.Description = Input.Description;
            location.OperationalRole = Input.OperationalRole; location.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        var location = await dbContext.Locations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == DeleteInput.LocationId, cancellationToken);
        if (location is null) return NotFound();
        if (location.Kind != LocationKind.Area) return BadRequest();
        Input = new()
        {
            Id = location.Id,
            Code = location.Code,
            Description = location.Description,
            OperationalRole = location.OperationalRole
        };
        Deletion = await areas.GetDeletionStateAsync(location.Id, cancellationToken);
        var result = await areas.DeleteAsync(new LocationAreaDeleteCommand(DeleteInput.OperationId,
            CurrentUserId(), DeleteInput.LocationId, DeleteInput.Reason, DeleteInput.Pin,
            DeleteInput.ConfirmationCode), cancellationToken);
        DeleteInput.Pin = string.Empty;
        ModelState.Remove($"{nameof(DeleteInput)}.{nameof(DeleteInputModel.Pin)}");
        if (result.Status == LocationAreaDeleteStatus.Success)
        {
            TempData["Message"] = $"Se eliminó definitivamente el área {location.Code}.";
            return RedirectToPage("Index");
        }
        if (result.Status == LocationAreaDeleteStatus.NotFound) return NotFound();
        DeleteErrors = result.Status switch
        {
            LocationAreaDeleteStatus.InvalidPin => ["No fue posible validar el NIP de un ADMIN activo."],
            LocationAreaDeleteStatus.Unauthorized => ["La sesión ADMIN ya no es válida."],
            LocationAreaDeleteStatus.IdempotencyConflict => ["La operación ya fue utilizada con otro contenido."],
            _ => result.Errors ?? ["No fue posible eliminar el área."]
        };
        Deletion = await areas.GetDeletionStateAsync(location.Id, cancellationToken);
        return Page();
    }

    private async Task PrepareDeletionAsync(Guid locationId, CancellationToken token)
    {
        if (locationId == Guid.Empty) return;
        Deletion = await areas.GetDeletionStateAsync(locationId, token);
        if (Deletion is null) return;
        if (DeleteInput.OperationId == Guid.Empty) DeleteInput.OperationId = Guid.NewGuid();
        DeleteInput.LocationId = locationId;
    }

    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : Guid.Empty;

    public sealed class InputModel
    {
        public Guid Id { get; set; }
        [Required, StringLength(40)] public string Code { get; set; } = string.Empty;
        [StringLength(200)] public string? Description { get; set; }
        public LocationOperationalRole OperationalRole { get; set; } = LocationOperationalRole.Other;
    }

    public sealed class DeleteInputModel
    {
        public Guid OperationId { get; set; }
        public Guid LocationId { get; set; }
        public string? Reason { get; set; }
        public string Pin { get; set; } = string.Empty;
        public string? ConfirmationCode { get; set; }
    }
}
