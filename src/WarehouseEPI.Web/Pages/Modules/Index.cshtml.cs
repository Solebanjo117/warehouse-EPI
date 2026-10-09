using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Web.Navigation;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Modules;

public sealed class IndexModel(IAuthorizationService authorization, StagingArrivalQuery staging) : PageModel
{
    public NavigationModule Module { get; private set; } = null!;
    public int StagingPendingCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(string module)
    {
        var isAdmin = User.IsInRole("ADMIN");
        var definition = ModuleNavigation.Find(module, isAdmin);
        if (definition is null) return NotFound();
        var visible = ModuleNavigation.GetVisible(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value);
        if (!visible.Any(item => item.Key == definition.Key) && !(await authorization.AuthorizeAsync(User, null, "AdminOnly")).Succeeded)
            return User.Identity?.IsAuthenticated == true ? Forbid() : Challenge();
        Module = visible.Single(item => item.Key == definition.Key);
        if (Module.Key == "operations")
            StagingPendingCount = await staging.CountPendingAsync(HttpContext.RequestAborted);
        return Page();
    }
}
