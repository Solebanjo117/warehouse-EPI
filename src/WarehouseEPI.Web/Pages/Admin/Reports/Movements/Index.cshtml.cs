using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WarehouseEPI.Web.Pages.Admin.Reports.Movements;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        var query = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
        if (!Request.Query.ContainsKey("view"))
            query = $"{query}{(string.IsNullOrEmpty(query) ? "?" : "&")}view=effective";
        return Redirect($"/Admin/Inventory/Movements{query}");
    }

    public IActionResult OnGetExport() => OnGet();
}
