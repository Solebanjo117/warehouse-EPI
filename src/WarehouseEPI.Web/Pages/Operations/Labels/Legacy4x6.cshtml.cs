using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace WarehouseEPI.Web.Pages.Operations.Labels;
public sealed class Legacy4x6Model : PageModel { public IActionResult OnGet() => RedirectToPagePermanent("/Operations/Labels/Index", new { Code = "LBL-6X4-ZEBRA" }); }
