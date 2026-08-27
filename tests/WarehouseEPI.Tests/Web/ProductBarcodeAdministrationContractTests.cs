namespace WarehouseEPI.Tests.Web;

public sealed class ProductBarcodeAdministrationContractTests
{
    [Fact]
    public void Product_pages_remove_barcode_administration_but_keep_historical_search()
    {
        var create = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Create.cshtml");
        var edit = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Edit.cshtml");
        var editModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Edit.cshtml.cs");
        var details = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Details.cshtml");
        var index = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Index.cshtml");
        var catalog = Read("src", "WarehouseEPI.Infrastructure", "Catalogs", "ProductCatalogQueryService.cs");

        Assert.DoesNotContain("BarcodeInput", create, StringComparison.Ordinal);
        Assert.DoesNotContain("Códigos de barras", edit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OnPostAddBarcodeAsync", editModel, StringComparison.Ordinal);
        Assert.DoesNotContain("OnPostToggleBarcodeAsync", editModel, StringComparison.Ordinal);
        Assert.DoesNotContain("OnPostSetPrimaryAsync", editModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Códigos de barras", details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<section class=\"card\"><div class=\"card-body\"><h2 class=\"h4\">Lotes internos", details, StringComparison.Ordinal);
        Assert.Contains("placeholder=\"SKU, descripción, referencia o ubicación\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain("BarcodeCount", index, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductCatalogBarcode", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("BarcodeCount", catalog, StringComparison.Ordinal);
        Assert.Contains("p.Barcodes.Any", catalog, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepositoryPath(parts));

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;

        if (directory is null)
            throw new DirectoryNotFoundException("No se encontró la raíz de Warehouse EPI.");

        return Path.Combine([directory.FullName, .. parts]);
    }
}
