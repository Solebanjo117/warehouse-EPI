using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WarehouseEPI.Web.Pages.Admin.Production;
[Authorize(Policy="AdminOnly")]
public sealed class DetailsModel:PageModel
{
 public IActionResult OnGet(Guid id)=>RedirectToPage("/Operations/Production/Work",new{id});
}
