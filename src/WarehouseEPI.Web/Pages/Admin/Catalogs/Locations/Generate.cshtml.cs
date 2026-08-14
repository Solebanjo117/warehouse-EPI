using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Web.Locations;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class GenerateModel(LocationGenerationService generationService) : PageModel
{
    [BindProperty] public string Manifest { get; set; } = "A,1,10,1-9";
    [BindProperty] public string[] SelectedCodes { get; set; } = [];
    public LocationGenerationPreview? Preview { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public IActionResult OnGet(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return Page();
        if (!TryOwner(out var owner) || !generationService.TryGetPreview(token, owner, out var preview) || preview is null)
        { Error = "La vista previa expiró, ya fue utilizada o no te pertenece."; return RedirectToPage(); }
        Preview = preview; Manifest = preview.Manifest; SelectedCodes = preview.Rows.Where(row => !row.Exists).Select(row => row.Code).ToArray();
        return Page();
    }

    public async Task<IActionResult> OnPostPrepareAsync(CancellationToken cancellationToken)
    {
        if (!TryOwner(out var owner)) return Forbid();
        var preview = await generationService.PrepareAsync(Manifest, owner, cancellationToken);
        return RedirectToPage(new { token = preview.Token });
    }

    public async Task<IActionResult> OnPostConfirmAsync(string token, CancellationToken cancellationToken)
    {
        if (!TryOwner(out var owner)) return Forbid();
        var result = await generationService.ConfirmAsync(token, owner, SelectedCodes, cancellationToken);
        if (!result.Succeeded) { Error = result.ErrorMessage; return RedirectToPage(new { token }); }
        Message = $"Se cargaron {result.Inserted:N0} ubicaciones validadas.";
        return RedirectToPage();
    }

    private bool TryOwner(out Guid owner) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out owner);
}
