namespace WarehouseEPI.Tests.Web;

public sealed class ProductCatalogIndexContractTests
{
    [Fact]
    public void Product_catalog_exposes_quick_filters_and_preserves_get_contract()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Index.cshtml");

        Assert.Contains("aria-label=\"Filtros rápidos del catálogo\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-status=\"active\" asp-route-stock=\"negative\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-status=\"active\" asp-route-stock=\"minimum\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-assignment=\"unassigned\" asp-route-pageNumber=\"1\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"search\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"status\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"stock\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"assignment\"", page, StringComparison.Ordinal);
        Assert.Contains("Limpiar filtros", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Product_catalog_search_can_submit_a_live_camera_scan_through_the_existing_get_form()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Index.cshtml");
        var script = Read("src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js");

        Assert.Contains("data-product-catalog-search-form", page, StringComparison.Ordinal);
        Assert.Contains("data-product-catalog-camera", page, StringComparison.Ordinal);
        Assert.Contains("data-product-catalog-camera-modal", page, StringComparison.Ordinal);
        Assert.Contains("data-lookup-url=\"@Url.Page(\"/Operations/Lookup\")\"", page, StringComparison.Ordinal);
        Assert.Contains("data-camera-video", page, StringComparison.Ordinal);
        Assert.Contains("data-camera-switch", page, StringComparison.Ordinal);
        Assert.Contains("data-camera-photo", page, StringComparison.Ordinal);
        Assert.Contains("zxing-browser.min.js", page, StringComparison.Ordinal);
        Assert.Contains("const createCameraScanner =", script, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(script, "createCameraScanner({"));
        Assert.Contains("handler: \"ResolveInventoryCode\"", script, StringComparison.Ordinal);
        Assert.Contains("if (!resolution?.product)", script, StringComparison.Ordinal);
        Assert.Contains("El código corresponde a una ubicación, no a un producto.", script, StringComparison.Ordinal);
        Assert.Contains("return { accepted: true, sku: resolution.product.sku }", script, StringComparison.Ordinal);
        Assert.Contains("if (result?.accepted)", script, StringComparison.Ordinal);
        Assert.Contains("productCatalogForm.requestSubmit()", script, StringComparison.Ordinal);
        Assert.Contains("stream.getTracks().forEach(track => track.stop())", script, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"pagehide\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("const submitScannedCode", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Product_catalog_has_desktop_table_and_tablet_cards_with_detail_first()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Index.cshtml");
        var styles = Read("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css");

        Assert.Contains("product-catalog-table d-none d-lg-block", page, StringComparison.Ordinal);
        Assert.Contains("product-catalog-card-list d-lg-none", page, StringComparison.Ordinal);
        Assert.Contains("product-catalog-empty", page, StringComparison.Ordinal);
        Assert.Contains("Mostrando @Model.FirstResult–@Model.LastResult", page, StringComparison.Ordinal);
        Assert.Contains("product-catalog-card", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 575.98px)", styles, StringComparison.Ordinal);

        var detail = page.IndexOf("asp-page=\"Details\"", StringComparison.Ordinal);
        var edit = page.IndexOf("asp-page=\"Edit\"", StringComparison.Ordinal);
        Assert.True(detail >= 0 && edit > detail, "La ficha debe permanecer antes que la edición administrativa.");
    }

    [Fact]
    public void Product_catalog_page_model_exposes_result_range_and_quick_filter_state()
    {
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Index.cshtml.cs");

        Assert.Contains("public int TotalResults", model, StringComparison.Ordinal);
        Assert.Contains("public int FirstResult", model, StringComparison.Ordinal);
        Assert.Contains("public int LastResult", model, StringComparison.Ordinal);
        Assert.Contains("public string? QuickFilter", model, StringComparison.Ordinal);
        Assert.Contains("(\"active\", \"all\", \"unassigned\") => \"unassigned\"", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Product_catalog_recipe_summary_is_lazy_accessible_and_admin_only()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Index.cshtml");
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Index.cshtml.cs");
        var script = Read("src", "WarehouseEPI.Web", "wwwroot", "js", "product-recipe-summary.js");
        var styles = Read("src", "WarehouseEPI.Web", "wwwroot", "css", "products-index.css");

        Assert.Contains("[Authorize(Policy = \"AdminOnly\")]", model, StringComparison.Ordinal);
        Assert.Contains("OnGetRecipeSummaryAsync", model, StringComparison.Ordinal);
        Assert.Contains("data-product-recipe-catalog", page, StringComparison.Ordinal);
        Assert.Contains("product-recipe-detail-row", page, StringComparison.Ordinal);
        Assert.Contains("product-recipe-mobile", page, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"false\"", page, StringComparison.Ordinal);
        Assert.Contains("data-product-recipe-trigger", page, StringComparison.Ordinal);
        Assert.Contains("visually-hidden-focusable product-recipe-keyboard-toggle", page, StringComparison.Ordinal);
        Assert.Contains("Receta v@(recipeVersion)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Ver receta", page, StringComparison.Ordinal);
        Assert.Contains("Sin receta activa", page, StringComparison.Ordinal);
        Assert.Contains("Configurar receta", page, StringComparison.Ordinal);
        Assert.Contains("Editar receta", page, StringComparison.Ordinal);
        Assert.Contains("asp-fragment=\"product-production\"", page, StringComparison.Ordinal);
        Assert.Contains("product-recipe-summary.js", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("const cache = new Map()", script, StringComparison.Ordinal);
        Assert.Contains("if (openButton) close(openButton)", script, StringComparison.Ordinal);
        Assert.Contains("[data-product-recipe-panel]", script, StringComparison.Ordinal);
        Assert.Contains("a, button, input, select, textarea, label", script, StringComparison.Ordinal);
        Assert.Contains("is-recipe-expanded", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Escape\"", script, StringComparison.Ordinal);
        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.Contains("table-layout: fixed", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1.2fr) minmax(0, 2fr) minmax(0, .8fr)", styles, StringComparison.Ordinal);
        Assert.Contains(".product-recipe-detail-row > td", styles, StringComparison.Ordinal);
        Assert.Contains("max-width: 100%", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("minmax(20rem, 2fr)", styles, StringComparison.Ordinal);
        Assert.Contains("padding: .5rem .625rem", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", styles, StringComparison.Ordinal);

        var detail = page.IndexOf("asp-page=\"Details\"", StringComparison.Ordinal);
        var edit = page.IndexOf("asp-page=\"Edit\"", StringComparison.Ordinal);
        Assert.True(detail >= 0 && edit > detail);
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepositoryPath(parts));

    private static int CountOccurrences(string source, string value) =>
        (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string RepositoryPath(params string[] parts)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
                directory = directory.Parent;

            if (directory is not null)
                return Path.Combine([directory.FullName, .. parts]);
        }

        throw new DirectoryNotFoundException("No se encontró la raíz de Warehouse EPI.");
    }
}
