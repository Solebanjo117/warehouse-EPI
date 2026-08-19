namespace WarehouseEPI.Tests.Web;

public sealed class RackViewContractTests
{
    [Fact]
    public void Rack_view_exposes_filters_panel_and_progressive_fallback()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml"));
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "rack-view.js"));

        Assert.Contains("asp-route-rackFilter", page, StringComparison.Ordinal);
        Assert.Contains("data-rack-open", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"Details\"", page, StringComparison.Ordinal);
        Assert.Contains("data-rack-detail", page, StringComparison.Ordinal);
        Assert.Contains("data-rack-close", page, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault()", script, StringComparison.Ordinal);
        Assert.Contains("event.key === 'Escape'", script, StringComparison.Ordinal);
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
