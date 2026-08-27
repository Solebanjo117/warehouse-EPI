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

    private static string Read(params string[] parts) => File.ReadAllText(RepositoryPath(parts));

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
