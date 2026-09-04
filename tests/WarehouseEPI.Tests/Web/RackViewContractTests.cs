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

    [Fact]
    public void Rack_administration_and_views_expose_the_whole_rack_wip_role()
    {
        var edit = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Rack", "Edit.cshtml"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml"));
        var details = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Details.cshtml"));
        var migration = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Infrastructure", "Persistence", "Migrations", "20260904120000_AllowRackWip.cs"));

        Assert.Contains("Input.OperationalRole", edit, StringComparison.Ordinal);
        Assert.Contains("WIP con inventario", edit, StringComparison.Ordinal);
        Assert.Contains("Se reclasificará el rack completo", edit, StringComparison.Ordinal);
        Assert.Contains("Rack WIP", page, StringComparison.Ordinal);
        Assert.Contains("position.IsWip", page, StringComparison.Ordinal);
        Assert.Contains("WIP · Pallet", details, StringComparison.Ordinal);
        Assert.Contains("DropCheckConstraint", migration, StringComparison.Ordinal);
        Assert.Contains("ck_locations_wip_area", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("DropTable", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_rack_wip_uses_the_physical_keypad_and_exact_position_actions()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml"));
        var styles = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css"));

        Assert.Contains("map-element-detail-wip-rack", page, StringComparison.Ordinal);
        Assert.Contains("element.Kind==\"Rack\"", page, StringComparison.Ordinal);
        Assert.Contains("new short[]{7,8,9,4,5,6,1,2,3}", page, StringComparison.Ordinal);
        Assert.Contains("data-map-position=\"@position.LocationId\"", page, StringComparison.Ordinal);
        Assert.Contains("data-position-detail=\"@position.LocationId\"", page, StringComparison.Ordinal);
        Assert.Contains("map-wip-position-actions", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-destinationLocationId=\"@position.LocationId\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-wipCode=\"@position.Code\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-wipAreaId=\"@position.LocationId\"", page, StringComparison.Ordinal);
        Assert.Contains("<details class=\"map-wip-summary", page, StringComparison.Ordinal);
        Assert.Contains("Resumen del rack WIP", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Posiciones WIP", page, StringComparison.Ordinal);
        Assert.Contains(".map-element-detail-wip-rack", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Rack_editor_exposes_guarded_permanent_deletion()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Rack", "Edit.cshtml"));
        var model = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Rack", "Edit.cshtml.cs"));

        Assert.Contains("Model.Rack.Deletion.CanDelete", page, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Delete\"", page, StringComparison.Ordinal);
        Assert.Contains("Esta acción no se puede deshacer", page, StringComparison.Ordinal);
        Assert.Contains("DeleteInput.ConfirmationCode", page, StringComparison.Ordinal);
        Assert.Contains("DeleteInput.Pin", page, StringComparison.Ordinal);
        Assert.Contains("OnPostDeleteAsync", model, StringComparison.Ordinal);
        Assert.Contains("LocationRackDeleteCommand", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_areas_expose_the_area_editor_for_wip_and_general_roles()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml"));

        Assert.Contains("asp-page=\"Area\" asp-route-locationId=\"@destinationId\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"Area\" asp-route-locationId=\"@position.LocationId\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"Rack/Edit\"", page, StringComparison.Ordinal);
        Assert.Contains(">Editar área</a>", page, StringComparison.Ordinal);
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
