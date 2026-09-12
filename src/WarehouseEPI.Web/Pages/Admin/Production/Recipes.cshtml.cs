using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WarehouseEPI.Web.Pages.Admin.Production;

[Authorize(Policy = "AdminOnly")]
public sealed class RecipesModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Admin/Catalogs/Products/Index");
    public IActionResult OnPost() => RedirectToPage("/Admin/Catalogs/Products/Index");
}
