namespace WarehouseEPI.Tests.Web;

public sealed class InventoryAnalyticsRouteTests
{
    [Fact]
    public void Inventory_analytics_is_public_read_only_and_exposes_expected_tabs_and_exports()
    {
        var page = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml"));
        var pageModel = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml.cs"));

        Assert.Contains("@page \"/Reports/Inventory\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorize", pageModel, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"occupancy\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"rotation\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"stagnant\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Export\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-format=\"xlsx\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-format=\"csv\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Inventory/Index\"", page, StringComparison.Ordinal);
        Assert.Contains("OnGetExportAsync", pageModel, StringComparison.Ordinal);
        Assert.Contains("10000", pageModel, StringComparison.Ordinal);
        Assert.Contains("La ocupación no se exporta", pageModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Administrative_links_are_conditional_and_navigation_connects_dashboard_and_analytics()
    {
        var page = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml"));
        var layout = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml"));
        var dashboard = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Dashboard", "Index.cshtml"));

        Assert.Contains("User.IsInRole(\"ADMIN\")", page, StringComparison.Ordinal);
        Assert.Contains("if (isAdmin)", page, StringComparison.Ordinal);
        Assert.Contains("/Admin/Catalogs/Products/Details", page, StringComparison.Ordinal);
        Assert.Contains("/Admin/Inventory/Alerts", page, StringComparison.Ordinal);
        Assert.Contains("/Admin/Catalogs/Locations/Index", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Reports/Inventory/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("Analítica de inventario", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Reports/Inventory/Index\"", dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_model_normalizes_supported_filters_and_uses_filter_specific_cache()
    {
        var pageModel = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml.cs"));

        Assert.Contains("TimeSpan.FromSeconds(60)", pageModel, StringComparison.Ordinal);
        Assert.Contains("PageSize = 25", pageModel, StringComparison.Ordinal);
        Assert.Contains("view is \"rotation\" or \"stagnant\"", pageModel, StringComparison.Ordinal);
        Assert.Contains("period is \"30\" or \"180\" or \"all\"", pageModel, StringComparison.Ordinal);
        Assert.Contains("status is \"inactive\" or \"all\"", pageModel, StringComparison.Ordinal);
        Assert.Contains("filter.ProductStatus", pageModel, StringComparison.Ordinal);
        Assert.Contains("filter.Search", pageModel, StringComparison.Ordinal);
        Assert.Contains("filter.UnitId", pageModel, StringComparison.Ordinal);
        Assert.Contains("filter.PageNumber", pageModel, StringComparison.Ordinal);
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
