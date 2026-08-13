using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Web.Pages.Admin;

[AllowAnonymous]
public sealed class LoginModel(UserPinService userPinService) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        return User.IsInRole("ADMIN")
            ? RedirectToPage("/Admin/Users/Index")
            : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await userPinService.AuthenticateAsync(Input.Pin, cancellationToken);
        if (user is null || user.Role.Code != "ADMIN")
        {
            ModelState.AddModelError(string.Empty, "NIP inválido o sin permiso administrativo.");
            return Page();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Role, user.Role.Code)
        };
        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30)
            });

        return !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : RedirectToPage("/Admin/Users/Index");
    }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "El NIP es obligatorio.")]
        [RegularExpression("^[0-9]{4,8}$", ErrorMessage = "Use entre 4 y 8 dígitos.")]
        [DataType(DataType.Password)]
        public string Pin { get; set; } = string.Empty;
    }
}
