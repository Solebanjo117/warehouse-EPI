namespace WarehouseEPI.Tests.Web;

public sealed class PublicLocationContractTests
{
    [Fact]
    public void Public_location_pages_are_get_only_and_admin_writes_remain_separate()
    {
        var publicIndex = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "Index.cshtml.cs");
        var publicDetail = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "Details.cshtml.cs");
        var publicDetailPage = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "Details.cshtml");
        var adminIndex = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml.cs");
        var adminDetail = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Details.cshtml.cs");

        Assert.DoesNotContain("OnPost", publicIndex, StringComparison.Ordinal);
        Assert.DoesNotContain("OnPost", publicDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"post\"", publicDetailPage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OnPostToggleAsync", adminIndex, StringComparison.Ordinal);
        Assert.Contains("OnPostBlockAsync", adminIndex, StringComparison.Ordinal);
        Assert.Contains("OnPostAssignAsync", adminDetail, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = \"AdminOnly\")]", adminIndex, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = \"AdminOnly\")]", adminDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_and_inventory_use_public_location_routes()
    {
        var layout = Read("src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml");
        var inventory = Read("src", "WarehouseEPI.Web", "Pages", "Inventory", "Index.cshtml");
        var query = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "_LocationIndex.cshtml");

        Assert.Contains(ModuleNavigationTestSupport.Actions(), action => action.Page == "/Admin/Catalogs/Locations/Index");
        Assert.Contains(ModuleNavigationTestSupport.Actions(false), action => action.Page == "/Locations/Index");
        Assert.Equal("inventory", WarehouseEPI.Web.Navigation.ModuleNavigation.Active("/Locations/Details", null, false)?.Key);
        Assert.Single(ModuleNavigationTestSupport.Actions(), action => action.Title == "Ubicaciones");
        Assert.DoesNotContain("<span>Administrar ubicaciones</span>", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Locations/Details\"", inventory, StringComparison.Ordinal);
        Assert.Contains("value=\"unavailable\"", query, StringComparison.Ordinal);
        Assert.Contains("No disponibles", query, StringComparison.Ordinal);
        Assert.Contains("data-map-position-match", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_location_map_exposes_heatmap_modes_without_a_duplicate_navigation_entry()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "_LocationIndex.cshtml");
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "LocationIndexPageModel.cs");
        var legacyPage = Read("src", "WarehouseEPI.Web", "Pages", "Reports", "Heatmap", "Index.cshtml");
        var legacyModel = Read("src", "WarehouseEPI.Web", "Pages", "Reports", "Heatmap", "Index.cshtml.cs");
        var layout = Read("src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml");
        var script = Read("src", "WarehouseEPI.Web", "wwwroot", "js", "warehouse-map-query.js");

        Assert.Contains("Mapa de calor", page, StringComparison.Ordinal);
        Assert.Contains("name=\"mapMetric\"", page, StringComparison.Ordinal);
        Assert.Contains("data-heat-level", page, StringComparison.Ordinal);
        Assert.Contains("Racks sin colocar", page, StringComparison.Ordinal);
        Assert.Contains("OnGetHeatmapExportAsync", model, StringComparison.Ordinal);
        Assert.Contains("OnGetHeatmapDataAsync", model, StringComparison.Ordinal);
        Assert.Contains("Heatmap.AllRacks", model, StringComparison.Ordinal);
        Assert.Contains("asp-route-rowCode=\"@Model.RowCode\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-search=\"@Model.Search\"", page, StringComparison.Ordinal);
        Assert.Contains("data-heatmap-submit", script, StringComparison.Ordinal);
        Assert.Contains("fetch(`${window.location.pathname}?${query}`", script, StringComparison.Ordinal);
        Assert.Contains("window.history.replaceState", script, StringComparison.Ordinal);
        Assert.Contains("map-heat-unavailable", page, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page=\"/Reports/Heatmap/Index\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", legacyPage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<h1", legacyPage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-heatmap-layer", legacyPage, StringComparison.Ordinal);
        Assert.Contains("/Admin/Catalogs/Locations/Index", legacyModel, StringComparison.Ordinal);
        Assert.Contains("HeatmapQueryNormalizer", legacyModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Location_search_reuses_inventory_autocomplete_for_products_and_locations()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "_LocationIndex.cshtml");
        var script = Read("src", "WarehouseEPI.Web", "wwwroot", "js", "location-index.js");
        var styles = Read("src", "WarehouseEPI.Web", "wwwroot", "css", "locations-index.css");

        Assert.Contains("data-location-search-url=\"@Url.Page(\"/Operations/Lookup\")\"", page, StringComparison.Ordinal);
        Assert.Contains("role=\"combobox\"", page, StringComparison.Ordinal);
        Assert.Contains("role=\"listbox\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-autocomplete=\"list\"", page, StringComparison.Ordinal);
        Assert.Contains("handler\", \"InventorySearch\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"ArrowDown\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Escape\"", script, StringComparison.Ordinal);
        Assert.Contains("form.requestSubmit()", script, StringComparison.Ordinal);
        Assert.Contains(".locations-index-workspace .location-search-results .list-group-item", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 auto", styles, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_detail_exposes_active_assignments_balances_and_safe_operations()
    {
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "LocationDetailsPageModel.cs");
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "Details.cshtml");

        Assert.Contains("IsAdministrativeView || assignment.IsActive", model, StringComparison.Ordinal);
        Assert.Contains("group.Sum(balance => balance.Quantity)", model, StringComparison.Ordinal);
        Assert.Contains("Saldo sin asignación", page, StringComparison.Ordinal);
        Assert.Contains("No disponible para nuevas operaciones", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Operations/Entry\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Operations/WipProcess\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Movimientos recientes", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Histórica", page, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
