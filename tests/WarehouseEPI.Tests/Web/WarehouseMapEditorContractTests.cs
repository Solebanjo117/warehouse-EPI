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
        Assert.Contains("selectionType", script, StringComparison.Ordinal);
        Assert.Contains("operationalMatches.length ? operationalMatches : architectureMatches", script, StringComparison.Ordinal);
        Assert.Contains("interaction.additive ? [...interaction.previous, ...matches]", script, StringComparison.Ordinal);
        Assert.Contains("matchedGroups.has(element.dataset.groupId)", script, StringComparison.Ordinal);
        Assert.Contains("targetElement && !layerIsLocked(layerCode(targetElement))", script, StringComparison.Ordinal);
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
    public void Query_and_editor_share_the_persisted_architecture_renderer()
    {
        var query = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml"));
        var editor = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml"));
        var renderer = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "_WarehouseMapArchitecture.cshtml"));
        var fallback = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "images", "warehouse-floor-base.svg"));

        Assert.Contains("_WarehouseMapArchitecture", query, StringComparison.Ordinal);
        Assert.Contains("_WarehouseMapArchitecture.cshtml", editor, StringComparison.Ordinal);
        Assert.Contains("data-architecture-element", renderer, StringComparison.Ordinal);
        Assert.Contains("data-architecture-layer", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("<image href=\"/images/warehouse-floor-base.svg\"", query, StringComparison.Ordinal);
        Assert.Contains("KPA / Breakroom", fallback, StringComparison.Ordinal);
        Assert.Contains("Packing / Producción", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_serializes_architecture_and_persisted_locks_without_publishing_layer_visibility()
    {
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "warehouse-map.js"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml"));

        Assert.Contains("data-editor-architecture", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-layer-state", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-layer-visible", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-layer-lock", page, StringComparison.Ordinal);
        Assert.Contains("warehouseEpi.mapEditor.layers.v1", script, StringComparison.Ordinal);
        Assert.Contains("CornerRadius: item.radius", script, StringComparison.Ordinal);
        Assert.Contains("Code: item.code, IsLocked: item.locked", script, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible: layerIsVisible", script, StringComparison.Ordinal);
        Assert.Contains("isArchitecture(current) !== isArchitecture(element)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("data-editor-draw", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-editor-delete", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_exposes_basic_architectural_drawing_properties_grid_snapping_and_zoom()
    {
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "warehouse-map.js"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml"));

        foreach (var tool in new[] { "select", "pan", "wall", "rectangle", "polygon", "door", "aisle", "zone", "text" })
            Assert.Contains($"data-editor-tool=\"{tool}\"", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-finish", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-cancel", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-discard-new", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-grid-pattern", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-properties", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-zoom-in", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-fit", page, StringComparison.Ordinal);
        Assert.Contains("crypto.randomUUID", script, StringComparison.Ordinal);
        Assert.Contains("normalizePolyline", script, StringComparison.Ordinal);
        Assert.Contains("data-editor-vertex", script, StringComparison.Ordinal);
        Assert.Contains("event.altKey", script, StringComparison.Ordinal);
        Assert.Contains("kind: \"pinch\"", script, StringComparison.Ordinal);
        Assert.Contains("warehouseEpi.mapEditor.workspace.v2", script, StringComparison.Ordinal);
        Assert.Contains("item.dataset.persisted !== \"true\"", script, StringComparison.Ordinal);
        Assert.Contains("?handler=Review", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_1192_migration_only_expands_architectural_style_tokens()
    {
        var migration = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Infrastructure", "Persistence", "Migrations", "20260824210000_ExpandWarehouseMapArchitectureStyles.cs"));

        Assert.Contains("ck_warehouse_map_architectural_style", migration, StringComparison.Ordinal);
        Assert.Contains("PRIMARY", migration, StringComparison.Ordinal);
        Assert.Contains("WARNING", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTable", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("warehouse_map_elements", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("locations", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory_", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_1193_exposes_productivity_scale_dimensions_archiving_and_server_review()
    {
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "warehouse-map.js"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml"));
        var pageModel = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Edit.cshtml.cs"));

        foreach (var contract in new[] { "data-editor-duplicate", "data-editor-group", "data-editor-ungroup", "data-editor-element-lock", "data-editor-order", "data-editor-archive", "data-editor-restore", "data-editor-measurement", "data-editor-scale", "data-editor-review-button", "data-editor-review-modal" })
            Assert.Contains(contract, page, StringComparison.Ordinal);
        Assert.Contains("data-editor-tool=\"dimension\"", page, StringComparison.Ordinal);
        Assert.Contains("data-editor-tool=\"calibrate\"", page, StringComparison.Ordinal);
        Assert.Contains("internalClipboard", script, StringComparison.Ordinal);
        Assert.Contains("groupId", script, StringComparison.Ordinal);
        Assert.Contains("formatDistance", script, StringComparison.Ordinal);
        Assert.Contains("updateDimensionLabels", script, StringComparison.Ordinal);
        Assert.Contains("archiveArchitecture", script, StringComparison.Ordinal);
        Assert.Contains("OnPostReviewAsync", pageModel, StringComparison.Ordinal);
        Assert.Contains("ReviewAsync", pageModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_1193_migration_only_extends_the_warehouse_map_tables()
    {
        var migration = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Infrastructure", "Persistence", "Migrations", "20260824233000_AddWarehouseMapProductivityScale.cs"));

        Assert.Contains("scale_units_per_inch", migration, StringComparison.Ordinal);
        Assert.Contains("measurement_system", migration, StringComparison.Ordinal);
        Assert.Contains("group_id", migration, StringComparison.Ordinal);
        Assert.Contains("is_archived", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTable", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("warehouse_map_elements", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("locations\"", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory_", migration, StringComparison.Ordinal);
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

    [Fact]
    public void Architecture_migration_only_adds_its_own_tables()
    {
        var migration = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Infrastructure", "Persistence", "Migrations", "20260824183000_AddWarehouseMapArchitecture.cs"));

        Assert.Contains("warehouse_map_layers", migration, StringComparison.Ordinal);
        Assert.Contains("warehouse_map_architectural_elements", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("AlterTable", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("AlterColumn", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("DropTable(name: \"warehouse_map_elements\")", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("locations\"", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory_", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_wip_panel_loads_and_renders_recent_issues_without_inventory_controls()
    {
        var pageModel = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml.cs"));
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Index.cshtml"));

        Assert.Contains("element.IsWip", pageModel, StringComparison.Ordinal);
        Assert.Contains("GetRecentIssuesAsync", pageModel, StringComparison.Ordinal);
        Assert.Contains("RecentWipIssues", pageModel, StringComparison.Ordinal);
        Assert.Contains("Últimos surtimientos", page, StringComparison.Ordinal);
        Assert.Contains("Aún no hay surtimientos registrados en este WIP.", page, StringComparison.Ordinal);
        Assert.Contains("/Reports/Wip/Details", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-wipAreaId", page, StringComparison.Ordinal);
        Assert.Contains("else\n            {\n                @if(element.Kind==\"Rack\")", page, StringComparison.Ordinal);
    }

    private static string RepositoryPath(params string[] parts)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configuredCandidate = Path.Combine([configuredRoot, .. parts]);
            if (File.Exists(configuredCandidate)) return configuredCandidate;
        }

        var workingDirectoryCandidate = Path.Combine([Directory.GetCurrentDirectory(), .. parts]);
        if (File.Exists(workingDirectoryCandidate)) return workingDirectoryCandidate;

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
