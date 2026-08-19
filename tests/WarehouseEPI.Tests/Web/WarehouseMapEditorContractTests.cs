namespace WarehouseEPI.Tests.Web;

public sealed class WarehouseMapEditorContractTests
{
    [Fact]
    public void Editor_exposes_group_selection_and_serializes_the_complete_geometry()
    {
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "warehouse-map.js"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml"));

        Assert.Contains("const selected = new Set()", script, StringComparison.Ordinal);
        Assert.Contains("kind: \"marquee\"", script, StringComparison.Ordinal);
        Assert.Contains("resizeGroup", script, StringComparison.Ordinal);
        Assert.Contains("data-editor-mirror", script, StringComparison.Ordinal);
        Assert.Contains("data-editor-group-resize", script, StringComparison.Ordinal);
        Assert.Contains("data-editor-multi", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-selection", page, StringComparison.Ordinal);
        Assert.Contains("IsVisible: item.isVisible", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_exposes_organization_tools_with_geometry_and_history_contracts()
    {
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "warehouse-map.js"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml"));

        Assert.Contains("alignmentCoordinate", script, StringComparison.Ordinal);
        Assert.Contains("distributedCenters", script, StringComparison.Ordinal);
        Assert.Contains("fitSize", script, StringComparison.Ordinal);
        Assert.Contains("const align =", script, StringComparison.Ordinal);
        Assert.Contains("const distribute =", script, StringComparison.Ordinal);
        Assert.Contains("const equalSize =", script, StringComparison.Ordinal);
        Assert.Contains("const undoAction", script, StringComparison.Ordinal);
        Assert.Contains("event.key.toLowerCase() === \"z\"", script, StringComparison.Ordinal);
        Assert.Contains("data-editor-align", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-distribute", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-size", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-reference", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_orders_only_selected_visible_racks_from_one_row()
    {
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "warehouse-map.js"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml"));

        Assert.Contains("const sortSelectedRow", script, StringComparison.Ordinal);
        Assert.Contains("items.length < 2", script, StringComparison.Ordinal);
        Assert.Contains("element.dataset.rowCode !== rowCode", script, StringComparison.Ordinal);
        Assert.Contains("element.dataset.visible !== \"true\"", script, StringComparison.Ordinal);
        Assert.Contains("dataset.rackNumber", script, StringComparison.Ordinal);
        Assert.Contains("data-editor-sort-row", page, StringComparison.Ordinal);
        Assert.Contains("data-row-code", page, StringComparison.Ordinal);
        Assert.Contains("data-rack-number", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_reload_replaces_the_operation_identifier_without_a_client_map_version()
    {
        var pageModel = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml.cs"));

        Assert.Contains("ModelState.Remove($\"{nameof(Input)}.{nameof(InputModel.OperationId)}\")", pageModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedVersion", pageModel, StringComparison.Ordinal);
        Assert.DoesNotContain("El croquis cambió mientras estaba abierto", pageModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_does_not_send_an_expected_map_version_when_saving()
    {
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "warehouse-map.js"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml"));

        Assert.DoesNotContain("ExpectedVersion", page, StringComparison.Ordinal);
        Assert.DoesNotContain("expectedVersion", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_service_only_persists_catalog_backed_new_elements_when_saving()
    {
        var service = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Infrastructure", "Locations", "WarehouseMapService.cs"));

        Assert.Contains("proposal.Where(item => stored.All", service, StringComparison.Ordinal);
        Assert.Contains("item.IsVisible = false", service, StringComparison.Ordinal);
        Assert.Contains("catalogById", service, StringComparison.Ordinal);
        Assert.Contains("El croquis contiene elementos que no existen en el catálogo actual.", service, StringComparison.Ordinal);
        Assert.Contains("layout.Elements.Add(element)", service, StringComparison.Ordinal);
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
