using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Web.Pages.Admin.Account;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(WarehouseDbContext dbContext, UserPinService pins) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync(cancellationToken);
        Input.FullName = user.FullName;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Input.FullName = Input.FullName.Trim();
        var user = await CurrentUserAsync(cancellationToken);
        var changingPin = !string.IsNullOrWhiteSpace(Input.NewPin) || !string.IsNullOrWhiteSpace(Input.ConfirmPin) || !string.IsNullOrWhiteSpace(Input.CurrentPin);
        if (changingPin)
        {
            if (!string.Equals(Input.NewPin, Input.ConfirmPin, StringComparison.Ordinal))
                ModelState.AddModelError("Input.ConfirmPin", "Los NIP no coinciden.");
            var current = await pins.AuthenticateAsync(Input.CurrentPin ?? string.Empty, cancellationToken);
            if (current?.Id != user.Id)
                ModelState.AddModelError("Input.CurrentPin", "El NIP actual no es válido.");
        }
        if (!ModelState.IsValid) return Page();
        if (changingPin)
        {
            var result = await pins.AssignAsync(user, Input.NewPin!, cancellationToken);
            if (result != PinAssignmentResult.Success)
            {
                ModelState.AddModelError("Input.NewPin", result == PinAssignmentResult.Duplicate ? "El NIP ya pertenece a otro usuario." : "Use un NIP de 4 a 8 dígitos.");
                return Page();
            }
        }
        user.FullName = Input.FullName;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await RenewSessionAsync(user.Id, user.FullName);
        TempData["Success"] = "Tu cuenta fue actualizada.";
        return RedirectToPage();
    }

    private async Task<WarehouseEPI.Core.Entities.User> CurrentUserAsync(CancellationToken cancellationToken) =>
        await dbContext.Users.Include(item => item.Role).SingleAsync(item => item.Id == Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), cancellationToken);

    private Task RenewSessionAsync(Guid id, string name) => HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Name, name), new(ClaimTypes.Role, "ADMIN")], CookieAuthenticationDefaults.AuthenticationScheme)));

    public sealed class InputModel
    {
        [Required, StringLength(160)] public string FullName { get; set; } = string.Empty;
        [RegularExpression("^[0-9]{4,8}$")] public string? CurrentPin { get; set; }
        [RegularExpression("^[0-9]{4,8}$")] public string? NewPin { get; set; }
        public string? ConfirmPin { get; set; }
    }
}
