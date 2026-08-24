namespace WarehouseEPI.Tests.Web;

public sealed class InventoryPageContractTests
{
    [Fact]
    public void Inventory_page_uses_read_only_admin_navigation_and_actionable_filter_counts()
    {
        var page = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Inventory", "Index.cshtml"));

        Assert.Contains("asp-page=\"/Admin/Catalogs/Products/Details\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page=\"/Admin/Catalogs/Products/Edit\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Admin/Inventory/Movements/Index\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Admin/Catalogs/Locations/Index\"", page, StringComparison.Ordinal);
        Assert.Contains("isAdmin && Model.Product is not null", page, StringComparison.Ordinal);
        Assert.Contains("Model.Results.Summary.AssignedZero", page, StringComparison.Ordinal);
        Assert.Contains("Asignado · saldo cero", page, StringComparison.Ordinal);
        Assert.Contains("Saldo sin asignación", page, StringComparison.Ordinal);
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
