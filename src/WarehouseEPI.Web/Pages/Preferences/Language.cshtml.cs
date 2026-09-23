using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Preferences;

public sealed class LanguageModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public IActionResult OnPost(string? language, string? returnUrl)
    {
        if (!UiLanguage.IsSupported(language))
            return BadRequest();

        Response.Cookies.Append(UiLanguage.CookieName, language!, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = Request.PathBase.HasValue ? Request.PathBase.Value : "/",
            MaxAge = TimeSpan.FromDays(365)
        });
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToPage("/Index");
    }
}
