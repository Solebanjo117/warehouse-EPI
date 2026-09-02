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

    [Fact]
    public void Rack_bays_render_as_elevations_and_reserve_color_for_incidents()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml"));
        var styles = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css"));

        Assert.Contains("rack-bay-frame", page, StringComparison.Ordinal);
        Assert.Contains("class=\"bay-slot bay-@position.RackState\"", page, StringComparison.Ordinal);
        Assert.Contains("bay-flag", page, StringComparison.Ordinal);
        Assert.Contains(".rack-bay-frame::after", styles, StringComparison.Ordinal);
        Assert.Contains(".bay-negative { background: var(--bs-danger-bg-subtle)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".bay-occupied {", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_table_labels_every_cell_for_the_mobile_fallback()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml"));
        var styles = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css"));

        foreach (var label in new[] { "Ubicación", "Posición física", "Productos asignados", "Estado", "Acciones" })
        {
            Assert.Contains($"data-label=\"{label}\"", page, StringComparison.Ordinal);
        }

        Assert.Contains("location-row-@AdminState(item)", page, StringComparison.Ordinal);
        Assert.Contains("content:attr(data-label)", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("table-striped align-middle inventory-list", page, StringComparison.Ordinal);
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
