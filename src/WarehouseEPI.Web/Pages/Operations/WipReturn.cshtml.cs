using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class WipReturnModel : PageModel
{
    public IActionResult OnGet(string? search, Guid? lineId)
    {
        return RedirectToPage("/Operations/WipProcess", new { action = "return" });
    }

    public IActionResult OnPost() => RedirectToPage("/Operations/WipProcess", new { action = "return" });

}
