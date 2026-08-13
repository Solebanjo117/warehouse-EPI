using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Web.Pages.Admin.Users;

[Authorize(Policy = "AdminOnly")]
public sealed class CreateModel(
    WarehouseDbContext dbContext,
    UserPinService userPinService) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> Roles { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadRolesAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Input.FullName = Input.FullName.Trim();

        if (!string.Equals(Input.Pin, Input.ConfirmPin, StringComparison.Ordinal))
        {
            ModelState.AddModelError("Input.ConfirmPin", "Los NIP no coinciden.");
        }

        var roleExists = await dbContext.Roles.AnyAsync(
            role => role.Id == Input.RoleId,
            cancellationToken);
        if (!roleExists)
        {
            ModelState.AddModelError("Input.RoleId", "Seleccione un rol válido.");
        }

        if (!ModelState.IsValid)
        {
            await LoadRolesAsync(cancellationToken);
            return Page();
        }

        var user = new User
        {
            FullName = Input.FullName,
            RoleId = Input.RoleId,
            PinLookup = string.Empty,
            PinHash = string.Empty,
            IsActive = Input.IsActive
        };

        var assignment = await userPinService.AssignAsync(user, Input.Pin, cancellationToken);
        if (assignment != PinAssignmentResult.Success)
        {
            AddPinError(assignment);
            await LoadRolesAsync(cancellationToken);
            return Page();
        }

        dbContext.Users.Add(user);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            ModelState.AddModelError("Input.Pin", "El NIP ya está asignado a otro usuario.");
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

    private void AddPinError(PinAssignmentResult result)
    {
        ModelState.AddModelError(
            "Input.Pin",
            result == PinAssignmentResult.Duplicate
                ? "El NIP ya está asignado a otro usuario."
                : "Use un NIP de 4 a 8 dígitos.");
    }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "El nombre es obligatorio.")]
        [StringLength(160, ErrorMessage = "El nombre no puede superar 160 caracteres.")]
        public string FullName { get; set; } = string.Empty;

        [Range(1, short.MaxValue, ErrorMessage = "Seleccione un rol.")]
        public short RoleId { get; set; }

        [Required(ErrorMessage = "El NIP es obligatorio.")]
        [RegularExpression("^[0-9]{4,8}$", ErrorMessage = "Use entre 4 y 8 dígitos.")]
        [DataType(DataType.Password)]
        public string Pin { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirme el NIP.")]
        [DataType(DataType.Password)]
        public string ConfirmPin { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;
    }
}
