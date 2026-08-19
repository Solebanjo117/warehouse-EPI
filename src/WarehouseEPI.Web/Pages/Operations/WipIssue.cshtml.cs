using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WarehouseEPI.Web.Pages.Operations;

public sealed class WipIssueModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Operations/Exit", new { mode = "wip" });
}
