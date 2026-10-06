using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin;

[AllowAnonymous]
public sealed class LoginModel(UserPinService userPinService, IStringLocalizer<CatalogTexts> text) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        return User.Identity?.IsAuthenticated == true
            ? RedirectToPage(Landing(User.FindFirstValue(ClaimTypes.Role)))
            : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Input.Pin = string.Empty; ModelState.Remove("Input.Pin");
            return Page();
        }

        var user = await userPinService.AuthenticateAsync(Input.Pin, cancellationToken);
        if (user is null || !WarehouseEPI.Core.Entities.RoleAccess.IsKnown(user.Role.Code))
        {
            ModelState.AddModelError(string.Empty, text["NIP inválido o usuario inactivo."].Value);
            Input.Pin = string.Empty; ModelState.Remove("Input.Pin");
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
            : RedirectToPage(Landing(user.Role.Code));
    }

    private static string Landing(string? role) => role switch
    {
        "ADMIN" => "/Admin/Users/Index",
        "PRODUCTION" => "/Operations/Production/Index",
        _ => "/Index"
    };

    public sealed class InputModel
    {
        [Required(ErrorMessage = "El NIP es obligatorio.")]
        [RegularExpression("^[0-9]{4,8}$", ErrorMessage = "Use entre 4 y 8 dígitos.")]
        [DataType(DataType.Password)]
        public string Pin { get; set; } = string.Empty;
    }
}
