using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Web.Imports;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
[RequestSizeLimit(ProductImportLimits.MaxRequestBytes)]
public sealed class ImportModel(ProductImportService importService) : PageModel
{
    private const int PageSize = 50;

    [BindProperty]
    public IFormFile? Upload { get; set; }
    public ProductImportPreview? Preview { get; private set; }
    public IReadOnlyList<ProductImportPreviewRow> Rows { get; private set; } = [];
    public string Filter { get; private set; } = "all";
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;

    [TempData]
    public string? ImportMessage { get; set; }
    [TempData]
    public string? ImportError { get; set; }

    public IActionResult OnGet(string? token, string filter = "all", int pageNumber = 1)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Page();
        if (!TryOwnerId(out var ownerId) || !importService.TryGetPreview(token, ownerId, out var preview) || preview is null)
        {
            ImportError = "La vista previa expiró, ya fue utilizada o no te pertenece. Vuelve a seleccionar el archivo.";
            return RedirectToPage();
        }

        LoadPreview(preview, filter, pageNumber);
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken cancellationToken)
    {
        if (Upload is null || Upload.Length == 0)
            ModelState.AddModelError(nameof(Upload), "Selecciona un archivo XLSX.");
        else
        {
            if (!string.Equals(Path.GetExtension(Upload.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
                ModelState.AddModelError(nameof(Upload), "Solo se aceptan archivos con extensión .xlsx.");
            if (Upload.Length > ProductImportLimits.MaxFileBytes)
                ModelState.AddModelError(nameof(Upload), "El archivo no puede superar 10 MB.");
        }

        if (!ModelState.IsValid || !TryOwnerId(out var ownerId))
            return Page();

        var upload = Upload!;
        await using var stream = upload.OpenReadStream();
        var preview = await importService.PrepareAsync(stream, upload.FileName, ownerId, cancellationToken);
        return RedirectToPage(new { token = preview.Token });
    }

    public async Task<IActionResult> OnPostConfirmAsync(string token, CancellationToken cancellationToken)
    {
        if (!TryOwnerId(out var ownerId))
            return Forbid();
        var result = await importService.ConfirmAsync(token, ownerId, cancellationToken);
        if (!result.Succeeded)
        {
            ImportError = result.ErrorMessage;
            return RedirectToPage(new { token });
        }

        ImportMessage = $"Importación terminada: {result.Inserted:N0} productos insertados, " +
            $"{result.SkippedExisting:N0} omitidos por SKU existente y {result.Consolidated:N0} duplicados consolidados.";
        return RedirectToPage();
    }

    private void LoadPreview(ProductImportPreview preview, string filter, int pageNumber)
    {
        Preview = preview;
        Filter = filter is "new" or "existing" or "consolidated" or "warnings" or "errors" ? filter : "all";
        IEnumerable<ProductImportPreviewRow> query = preview.Rows;
        query = Filter switch
        {
            "new" => query.Where(row => row.IsCandidate),
            "existing" => query.Where(row => row.IsExisting),
            "consolidated" => query.Where(row => row.IsConsolidated),
            "warnings" => query.Where(row => row.HasWarning),
            "errors" => query.Where(row => row.HasError),
            _ => query
        };
        var filtered = query.ToList();
        TotalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
        CurrentPage = Math.Clamp(pageNumber, 1, TotalPages);
        Rows = filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();
    }

    private bool TryOwnerId(out Guid ownerId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out ownerId);
}
