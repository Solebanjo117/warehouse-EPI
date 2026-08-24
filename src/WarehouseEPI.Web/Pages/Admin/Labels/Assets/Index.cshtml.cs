using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Labels;

namespace WarehouseEPI.Web.Pages.Admin.Labels.Assets;

public sealed class IndexModel(LabelAssetService assets) : PageModel
{
    public IReadOnlyList<LabelAssetView> Items { get; private set; } = [];
    [BindProperty] public IFormFile? Image { get; set; }

    public async Task OnGetAsync(CancellationToken token) => Items = await assets.GetAllAsync(token);

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken token)
    {
        if (Image is null) ModelState.AddModelError(nameof(Image), "Selecciona una imagen PNG o JPEG.");
        else if (Image.Length > LabelAssetService.MaxBytes) ModelState.AddModelError(nameof(Image), "La imagen debe pesar como máximo 1 MiB.");
        else
        {
            await using var stream = Image.OpenReadStream();
            using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, token);
            var result = await assets.UploadAsync(CurrentUserId(), Image.FileName, Image.ContentType, buffer.ToArray(), token);
            if (result.Error is null) { TempData["StatusMessage"] = result.Asset?.Name == Image.FileName ? "Imagen agregada." : "La imagen ya existía; se reutilizará por su hash."; return RedirectToPage(); }
            ModelState.AddModelError(nameof(Image), result.Error);
        }
        Items = await assets.GetAllAsync(token); return Page();
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, bool archived, CancellationToken token)
    {
        if (!await assets.SetArchivedAsync(id, archived, token)) return NotFound();
        TempData["StatusMessage"] = archived ? "Imagen archivada. Las versiones existentes siguen funcionando." : "Imagen restaurada.";
        return RedirectToPage();
    }
    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
