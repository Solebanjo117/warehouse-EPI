using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.Labels;

public sealed class SupplierSheetModel(WarehouseDbContext db, BarcodeRenderingService barcodes,
    IStringLocalizer<OperationsTexts> texts) : PageModel
{
    public const int MaximumProducts = 100;
    [BindProperty] public List<Guid> ProductIds { get; set; } = [];
    [BindProperty] public string? SupplierName { get; set; }
    [BindProperty] public string? SearchText { get; set; }
    [BindProperty] public bool AllProducts { get; set; }
    public List<SelectedProduct> Selection { get; } = [];
    public List<SheetRow> Rows { get; } = [];

    public Task<IActionResult> OnPostAsync(CancellationToken token) => BuildAsync(true, token);

    public Task<IActionResult> OnPostRestoreAsync(bool preview, CancellationToken token) => BuildAsync(preview, token);

    private async Task<IActionResult> BuildAsync(bool preview, CancellationToken token)
    {
        SupplierName = SupplierName?.Trim();
        if (SupplierName?.Length > 200)
            ModelState.AddModelError(nameof(SupplierName), texts["El nombre del proveedor admite hasta 200 caracteres."]);
        if (!AllProducts && ((preview && ProductIds.Count == 0) || ProductIds.Count > MaximumProducts))
            ModelState.AddModelError(string.Empty, texts["Selecciona entre 1 y 100 productos."]);
        if (!AllProducts && ProductIds.Distinct().Count() != ProductIds.Count)
            ModelState.AddModelError(string.Empty, texts["Hay productos repetidos. Quita las filas repetidas y vuelve a generar."]);

        var products = await db.Products.AsNoTracking().Where(p => AllProducts ? p.IsActive : ProductIds.Contains(p.Id)).OrderBy(p => p.Sku).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Sku, p.Description, p.IsActive }).ToDictionaryAsync(p => p.Id, token);
        foreach (var id in ProductIds)
        {
            if (!products.TryGetValue(id, out var product))
            {
                Selection.Add(new(id, texts["Producto no disponible"], id.ToString()));
                if (!AllProducts) ModelState.AddModelError(string.Empty, texts["El producto {0} ya no existe. Quítalo de la selección.", id]);
                continue;
            }
            Selection.Add(new(id, product.Sku, product.Description));
            if (!AllProducts && !product.IsActive)
                ModelState.AddModelError(string.Empty, texts["El producto {0} está inactivo. Quítalo de la selección.", product.Sku]);
        }
        if (!ModelState.IsValid || !preview) return Page();

        if (AllProducts && products.Count == 0)
        {
            ModelState.AddModelError(string.Empty, texts["No hay productos activos para generar la hoja."]);
            return Page();
        }

        var printable = AllProducts
            ? products.Values.Select(p => new SelectedProduct(p.Id, p.Sku, p.Description))
            : Selection;
        foreach (var product in printable)
        {
            try
            {
                // 150 mm usable within the 155.9 mm column, at the existing 203 DPI.
                var barcode = barcodes.RenderCode128Svg(product.Sku, new(Width: 5905, Height: 150));
                if (barcode.IsBelowRecommendedDensity)
                    ModelState.AddModelError(string.Empty, texts["El código de {0} es demasiado ancho para esta hoja. Revisa el SKU.", product.Sku]);
                else Rows.Add(new(product, barcode));
            }
            catch (ArgumentException)
            {
                ModelState.AddModelError(string.Empty, texts["El SKU {0} no es compatible con Code 128. Revisa el catálogo.", product.Sku]);
            }
        }
        if (!ModelState.IsValid) Rows.Clear();
        return Page();
    }

    public sealed record SelectedProduct(Guid Id, string Sku, string? Description);
    public sealed record SheetRow(SelectedProduct Product, BarcodeSvg Barcode);
}
