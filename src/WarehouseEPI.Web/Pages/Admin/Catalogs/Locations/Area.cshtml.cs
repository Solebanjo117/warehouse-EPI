using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Npgsql;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class AreaModel(WarehouseDbContext dbContext, LocationAreaAdministrationService areas,
    ProductionProcessConfigurationService processes, IStringLocalizer<CatalogTexts> text) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty] public DeleteInputModel DeleteInput { get; set; } = new();
    public LocationAreaDeletionState? Deletion { get; private set; }
    public IReadOnlyList<string> DeleteErrors { get; private set; } = [];
    public bool IsEdit => Input.Id != Guid.Empty;
    public IReadOnlyList<ProductionStage> Processes { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid? locationId, CancellationToken cancellationToken)
    {
        if (locationId is null) { await LoadProcessesAsync(cancellationToken, refreshVersion: true); return Page(); }
        var location = await dbContext.Locations.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == locationId, cancellationToken);
        if (location is null) return NotFound();
        if (location.Kind != LocationKind.Area) return BadRequest();
        var selected = await processes.AreaProcessIdsAsync(location.Id, cancellationToken);
        Input = new()
        {
            Id = location.Id,
            Code = location.Code,
            Description = location.Description,
            OperationalRole = location.OperationalRole,
            WarnOnMixedProducts = location.WarnOnMixedProducts,
            ProcessIds = selected,
            IsActive = location.IsActive,
            IsBlocked = location.IsBlocked,
            BlockReason = location.BlockReason,
            OriginalUpdatedAt = location.UpdatedAt.ToString("O")
        };
        await LoadProcessesAsync(cancellationToken, refreshVersion: true);
        await PrepareDeletionAsync(location.Id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Input.Code = LocationNormalization.NormalizeCode(Input.Code);
        Input.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        Input.BlockReason = Input.IsActive && Input.IsBlocked ? Input.BlockReason?.Trim() : null;
        Input.IsBlocked = Input.IsActive && Input.IsBlocked;
        if (!Input.IsBlocked) ModelState.Remove("Input.BlockReason");
        if (!Enum.IsDefined(Input.OperationalRole))
            ModelState.AddModelError("Input.OperationalRole", text["Selecciona una función operativa válida."].Value);
        if (Input.IsBlocked && string.IsNullOrWhiteSpace(Input.BlockReason))
            ModelState.AddModelError("Input.BlockReason", text["Escribe un motivo de bloqueo de hasta 200 caracteres."].Value);
        if (!LocationNormalization.IsValidAreaCode(Input.Code))
            ModelState.AddModelError("Input.Code", text["Usa letras, números y guiones, sin espacios externos ni guiones al inicio o final."].Value);
        if (!ModelState.IsValid)
        {
            await LoadProcessesAsync(cancellationToken);
            await PrepareDeletionAsync(Input.Id, cancellationToken);
            return Page();
        }
        if (await dbContext.Locations.AnyAsync(location => location.Code == Input.Code && location.Id != Input.Id, cancellationToken))
        {
            ModelState.AddModelError("Input.Code", text["Ya existe una ubicación con ese código."].Value);
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
            if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                await dbContext.Locations.FromSqlInterpolated(
                    $"SELECT * FROM locations WHERE id = {Input.Id} FOR UPDATE").LoadAsync(cancellationToken);
            target = await dbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == Input.Id, cancellationToken) ?? null!;
            if (target is null) return NotFound();
            if (target.Kind != LocationKind.Area) return BadRequest();
            if (!DateTimeOffset.TryParseExact(Input.OriginalUpdatedAt, "O", global::System.Globalization.CultureInfo.InvariantCulture,
                    global::System.Globalization.DateTimeStyles.None, out var originalUpdatedAt) || target.UpdatedAt != originalUpdatedAt)
            {
                ModelState.AddModelError(string.Empty, text["El área cambió mientras editabas. Recarga la página y revisa los cambios antes de guardar."].Value);
                await LoadProcessesAsync(cancellationToken);
                await PrepareDeletionAsync(Input.Id, cancellationToken);
                return Page();
            }
            var roleError = await WarehouseEPI.Infrastructure.Locations.LocationWipRules.ValidateChangeAsync(
                dbContext, target.Id, target.OperationalRole, Input.OperationalRole, cancellationToken);
            if (roleError is not null)
            {
                ModelState.AddModelError("Input.OperationalRole", text[roleError]);
                await LoadProcessesAsync(cancellationToken);
                await PrepareDeletionAsync(Input.Id, cancellationToken);
                return Page();
            }
            target.Code = Input.Code; target.Description = Input.Description;
            target.OperationalRole = Input.OperationalRole; target.UpdatedAt = DateTimeOffset.UtcNow;
        }
        target.IsActive = Input.IsActive;
        target.WarnOnMixedProducts = Input.WarnOnMixedProducts;
        target.IsBlocked = Input.IsBlocked;
        target.BlockReason = Input.BlockReason;
        var association = await processes.ApplyAreaAsync(target.Id, Input.OperationalRole, Input.ProcessIds, Input.ProcessConfigurationVersion, cancellationToken);
        if (association.Status != ProcessConfigurationStatus.Success)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            var associationMessage = association.Status == ProcessConfigurationStatus.ConcurrencyConflict
                ? "La configuración de procesos cambió mientras editabas. Recarga y vuelve a revisar."
                : association.Errors?.FirstOrDefault() ?? "No fue posible asociar los procesos.";
            ModelState.AddModelError(string.Empty, text[associationMessage].Value);
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
            ModelState.AddModelError(string.Empty, text["La configuración de procesos cambió mientras editabas. Recarga y vuelve a revisar."].Value);
            await LoadProcessesAsync(cancellationToken); await PrepareDeletionAsync(Input.Id, cancellationToken); return Page();
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            ModelState.AddModelError("Input.Code", text["Ya existe una ubicación con ese código."].Value);
            await LoadProcessesAsync(cancellationToken); await PrepareDeletionAsync(Input.Id, cancellationToken); return Page();
        }
        TempData["Message"] = text["Se guardó el área {0}.", target.Code].Value;
        return RedirectToPage("Details", new { id = target.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        // The deletion form does not submit the editor fields.
        foreach (var key in ModelState.Keys.Where(key => key.StartsWith("Input.", StringComparison.Ordinal)).ToArray())
            ModelState.Remove(key);
        var location = await dbContext.Locations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == DeleteInput.LocationId, cancellationToken);
        if (location is null) return NotFound();
        if (location.Kind != LocationKind.Area) return BadRequest();
        Input = new()
        {
            Id = location.Id,
            Code = location.Code,
            Description = location.Description,
            OperationalRole = location.OperationalRole,
            WarnOnMixedProducts = location.WarnOnMixedProducts,
            IsActive = location.IsActive,
            IsBlocked = location.IsBlocked,
            BlockReason = location.BlockReason,
            OriginalUpdatedAt = location.UpdatedAt.ToString("O"),
            ProcessIds = await processes.AreaProcessIdsAsync(location.Id, cancellationToken)
        };
        Deletion = await areas.GetDeletionStateAsync(location.Id, cancellationToken);
        var result = await areas.DeleteAsync(new LocationAreaDeleteCommand(DeleteInput.OperationId,
            CurrentUserId(), DeleteInput.LocationId, DeleteInput.Reason, DeleteInput.Pin,
            DeleteInput.ConfirmationCode), cancellationToken);
        DeleteInput.Pin = string.Empty;
        ModelState.Remove($"{nameof(DeleteInput)}.{nameof(DeleteInputModel.Pin)}");
        if (result.Status == LocationAreaDeleteStatus.Success)
        {
            TempData["Message"] = text["Se eliminó definitivamente el área {0}.", location.Code].Value;
            return RedirectToPage("Areas");
        }
        if (result.Status == LocationAreaDeleteStatus.NotFound) return NotFound();
        DeleteErrors = result.Status switch
        {
            LocationAreaDeleteStatus.InvalidPin => [text["No fue posible validar el NIP de un ADMIN activo."].Value],
            LocationAreaDeleteStatus.Unauthorized => [text["La sesión ADMIN ya no es válida."].Value],
            LocationAreaDeleteStatus.IdempotencyConflict => [text["La operación ya fue utilizada con otro contenido."].Value],
            _ => (result.Errors ?? ["No fue posible eliminar el área."]).Select(error => text[error].Value).ToArray()
        };
        Deletion = await areas.GetDeletionStateAsync(location.Id, cancellationToken);
        await LoadProcessesAsync(cancellationToken, refreshVersion: true);
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

    private async Task LoadProcessesAsync(CancellationToken token, bool refreshVersion = false)
    {
        var result = await processes.GetProcessesAsync(Input.ProcessIds, token);
        if (refreshVersion) Input.ProcessConfigurationVersion = result.Version;
        Processes = result.Processes;
    }

    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : Guid.Empty;

    public sealed class InputModel
    {
        public Guid Id { get; set; }
        [Required, StringLength(40)] public string Code { get; set; } = string.Empty;
        [StringLength(200)] public string? Description { get; set; }
        [EnumDataType(typeof(LocationOperationalRole))]
        public LocationOperationalRole OperationalRole { get; set; } = LocationOperationalRole.Other;
        public bool WarnOnMixedProducts { get; set; } = true;
        public bool IsActive { get; set; } = true;
        public bool IsBlocked { get; set; }
        [StringLength(200)] public string? BlockReason { get; set; }
        public string? OriginalUpdatedAt { get; set; }
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
