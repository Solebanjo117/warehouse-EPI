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
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class AreaModel(WarehouseDbContext dbContext, LocationAreaAdministrationService areas,
    ProductionProcessConfigurationService processes) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty] public DeleteInputModel DeleteInput { get; set; } = new();
    public LocationAreaDeletionState? Deletion { get; private set; }
    public IReadOnlyList<string> DeleteErrors { get; private set; } = [];
    public bool IsEdit => Input.Id != Guid.Empty;
    public IReadOnlyList<ProductionStage> Processes { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid? locationId, CancellationToken cancellationToken)
    {
        if (locationId is null) { await LoadProcessesAsync(cancellationToken); return Page(); }
        var location = await dbContext.Locations.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == locationId, cancellationToken);
        if (location is null) return NotFound();
        if (location.Kind != LocationKind.Area) return BadRequest();
        var selected = await processes.AreaProcessIdsAsync(location.Id, cancellationToken);
        Input = new() { Id = location.Id, Code = location.Code, Description = location.Description, OperationalRole = location.OperationalRole, ProcessIds = selected };
        await LoadProcessesAsync(cancellationToken);
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
            await LoadProcessesAsync(cancellationToken);
            await PrepareDeletionAsync(Input.Id, cancellationToken);
            return Page();
        }
        if (await dbContext.Locations.AnyAsync(location => location.Code == Input.Code && location.Id != Input.Id, cancellationToken))
        {
            ModelState.AddModelError("Input.Code", "Ya existe una ubicación con ese código.");
            await LoadProcessesAsync(cancellationToken);
            await PrepareDeletionAsync(Input.Id, cancellationToken);
            return Page();
        }
        await using var transaction = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        Location target;
        if (Input.Id == Guid.Empty)
        {
            target = new Location { Code = Input.Code, Kind = LocationKind.Area, Description = Input.Description, OperationalRole = Input.OperationalRole };
            dbContext.Locations.Add(target);
        }
        else
        {
            target = await dbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == Input.Id, cancellationToken) ?? null!;
            if (target is null) return NotFound();
            if (target.Kind != LocationKind.Area) return BadRequest();
            target.Code = Input.Code; target.Description = Input.Description;
            target.OperationalRole = Input.OperationalRole; target.UpdatedAt = DateTimeOffset.UtcNow;
        }
        var association = await processes.ApplyAreaAsync(target.Id, Input.OperationalRole, Input.ProcessIds, Input.ProcessConfigurationVersion, cancellationToken);
        if (association.Status != ProcessConfigurationStatus.Success)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError(string.Empty, association.Status == ProcessConfigurationStatus.ConcurrencyConflict
                ? "La configuración de procesos cambió mientras editabas. Recarga y vuelve a revisar."
                : association.Errors?.FirstOrDefault() ?? "No fue posible asociar los procesos.");
            await LoadProcessesAsync(cancellationToken); await PrepareDeletionAsync(Input.Id, cancellationToken); return Page();
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError(string.Empty, "La configuración de procesos cambió mientras editabas. Recarga y vuelve a revisar.");
            await LoadProcessesAsync(cancellationToken); await PrepareDeletionAsync(Input.Id, cancellationToken); return Page();
        }
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

    private async Task LoadProcessesAsync(CancellationToken token)
    {
        var result = await processes.GetProcessesAsync(Input.ProcessIds, token);
        Input.ProcessConfigurationVersion = result.Version;
        Processes = result.Processes;
    }

    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : Guid.Empty;

    public sealed class InputModel
    {
        public Guid Id { get; set; }
        [Required, StringLength(40)] public string Code { get; set; } = string.Empty;
        [StringLength(200)] public string? Description { get; set; }
        public LocationOperationalRole OperationalRole { get; set; } = LocationOperationalRole.Other;
        public List<Guid> ProcessIds { get; set; } = [];
        public uint ProcessConfigurationVersion { get; set; }
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
