using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Web.Pages.Admin.Users;

[Authorize(Policy = "AdminOnly")]
public sealed class EditModel(
    WarehouseDbContext dbContext,
    UserPinService userPinService) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> Roles { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Id = user.Id,
            FullName = user.FullName,
            RoleId = user.RoleId,
            IsActive = user.IsActive
        };
        await LoadRolesAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Input.FullName = Input.FullName.Trim();
        var pinWasProvided = !string.IsNullOrWhiteSpace(Input.NewPin) ||
            !string.IsNullOrWhiteSpace(Input.ConfirmPin);

        if (pinWasProvided && !string.Equals(Input.NewPin, Input.ConfirmPin, StringComparison.Ordinal))
        {
            ModelState.AddModelError("Input.ConfirmPin", "Los NIP no coinciden.");
        }

        var selectedRole = await dbContext.Roles
            .SingleOrDefaultAsync(role => role.Id == Input.RoleId, cancellationToken);
        if (selectedRole is null)
        {
            ModelState.AddModelError("Input.RoleId", "Seleccione un rol válido.");
        }

        var user = await dbContext.Users
            .Include(candidate => candidate.Role)
            .SingleOrDefaultAsync(candidate => candidate.Id == Input.Id, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var removesAdminAccess = selectedRole?.Code != "ADMIN" || !Input.IsActive;
        if (user.Id == currentUserId && removesAdminAccess)
        {
            ModelState.AddModelError(string.Empty, "No puede quitarse su propio acceso administrativo.");
        }

        if (user.Role.Code == "ADMIN" && user.IsActive && removesAdminAccess)
        {
            var activeAdminCount = await dbContext.Users.CountAsync(
                candidate => candidate.IsActive && candidate.Role.Code == "ADMIN",
                cancellationToken);
            if (activeAdminCount <= 1)
            {
                ModelState.AddModelError(string.Empty, "Debe permanecer al menos un administrador activo.");
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadRolesAsync(cancellationToken);
            return Page();
        }

        if (pinWasProvided)
        {
            var assignment = await userPinService.AssignAsync(
                user,
                Input.NewPin!,
                cancellationToken);
            if (assignment != PinAssignmentResult.Success)
            {
                ModelState.AddModelError(
                    "Input.NewPin",
                    assignment == PinAssignmentResult.Duplicate
                        ? "El NIP ya está asignado a otro usuario."
                        : "Use un NIP de 4 a 8 dígitos.");
                await LoadRolesAsync(cancellationToken);
                return Page();
            }
        }

        user.FullName = Input.FullName;
        user.RoleId = Input.RoleId;
        user.IsActive = Input.IsActive;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            ModelState.AddModelError("Input.NewPin", "El NIP ya está asignado a otro usuario.");
            await LoadRolesAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage("Index");
    }

    private async Task LoadRolesAsync(CancellationToken cancellationToken)
    {
        Roles = await dbContext.Roles
            .AsNoTracking()
            .OrderBy(role => role.Id)
            .Select(role => new SelectListItem(role.Name, role.Id.ToString()))
            .ToListAsync(cancellationToken);
    }

    public sealed class InputModel
    {
        public Guid Id { get; set; }

        [Required(ErrorMessage = "El nombre es obligatorio.")]
        [StringLength(160, ErrorMessage = "El nombre no puede superar 160 caracteres.")]
        public string FullName { get; set; } = string.Empty;

        [Range(1, short.MaxValue, ErrorMessage = "Seleccione un rol.")]
        public short RoleId { get; set; }

        [RegularExpression("^[0-9]{4,8}$", ErrorMessage = "Use entre 4 y 8 dígitos.")]
        [DataType(DataType.Password)]
        public string? NewPin { get; set; }

        [DataType(DataType.Password)]
        public string? ConfirmPin { get; set; }

        public bool IsActive { get; set; }
    }
}
