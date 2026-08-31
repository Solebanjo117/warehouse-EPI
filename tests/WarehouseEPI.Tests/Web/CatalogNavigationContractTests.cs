namespace WarehouseEPI.Tests.Web;

public sealed class CatalogNavigationContractTests
{
    [Fact]
    public void Product_type_and_class_catalogs_are_in_admin_navigation()
    {
        var layout = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml"));

        Assert.Contains("asp-page=\"/Admin/Catalogs/ProductTypes/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Admin/Catalogs/ProductClasses/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("IsSection(\"/Admin/Catalogs/ProductTypes\")", layout, StringComparison.Ordinal);
        Assert.Contains("IsSection(\"/Admin/Catalogs/ProductClasses\")", layout, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"@(IsSection(\"/Admin/Catalogs/ProductTypes\") ? \"page\" : null)\"", layout, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"@(IsSection(\"/Admin/Catalogs/ProductClasses\") ? \"page\" : null)\"", layout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ProductTypes", "Tipos registrados")]
    [InlineData("ProductClasses", "Clases registradas")]
    public void Product_classification_pages_keep_admin_and_responsive_contracts(string directory, string heading)
    {
        var page = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", directory, "Index.cshtml"));
        var pageModel = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", directory, "Index.cshtml.cs"));

        Assert.Contains("[Authorize(Policy = \"AdminOnly\")]", pageModel, StringComparison.Ordinal);
        Assert.Contains(heading, page, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Save\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Toggle\"", page, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", page, StringComparison.Ordinal);
        Assert.Contains("@section Styles", page, StringComparison.Ordinal);
        Assert.Contains("scope=\"col\"", page, StringComparison.Ordinal);
        Assert.Contains("<caption", page, StringComparison.Ordinal);
        Assert.Contains("Todavía no hay", page, StringComparison.Ordinal);

        // Responsive con una sola tabla que el CSS reordena en fichas: ni marcado
        // duplicado por utilidades de visibilidad ni un segundo recorrido.
        Assert.DoesNotContain("d-none d-md-block", page, StringComparison.Ordinal);
        Assert.DoesNotContain("d-md-none", page, StringComparison.Ordinal);
        Assert.Equal(1, page.Split("@foreach", StringSplitOptions.None).Length - 1);
    }

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
