using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Web.Navigation;

namespace WarehouseEPI.Web.Pages.Modules;

public sealed class IndexModel(IAuthorizationService authorization) : PageModel
{
    public NavigationModule Module { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(string module)
    {
        var isAdmin = User.IsInRole("ADMIN");
        var definition = ModuleNavigation.Find(module, isAdmin);
        if (definition is null) return NotFound();
        if (definition.AdminOnly && !(await authorization.AuthorizeAsync(User, null, "AdminOnly")).Succeeded)
            return User.Identity?.IsAuthenticated == true ? Forbid() : Challenge();
        Module = ModuleNavigation.GetVisible(isAdmin).Single(item => item.Key == definition.Key);
        return Page();
    }
}
