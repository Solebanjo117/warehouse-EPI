namespace WarehouseEPI.Tests.Web;

public sealed class LocationDetailsContractTests
{
    [Fact]
    public void Location_detail_exposes_inventory_relationships_current_position_and_responsive_rack_cards()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Details.cshtml"));
        var content = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Locations", "_LocationRackPositionContent.cshtml"));
        var styles = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css"));

        Assert.Contains("Posiciones del rack", page, StringComparison.Ordinal);
        Assert.Contains("Leyenda de estados de inventario", page, StringComparison.Ordinal);
        Assert.Contains("Saldo sin asignación", page, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"location\"", page, StringComparison.Ordinal);
        Assert.Contains("posición inexistente", page, StringComparison.Ordinal);
        Assert.Contains("_LocationRackPositionContent", page, StringComparison.Ordinal);
        Assert.Contains("Actual", content, StringComparison.Ordinal);
        Assert.Contains("PrimaryBalance.Quantity", content, StringComparison.Ordinal);
        Assert.Contains("AdditionalProductCount", content, StringComparison.Ordinal);
        Assert.Contains("location-neighbor-grid", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns:repeat(3,minmax(0,1fr))", styles, StringComparison.Ordinal);
        Assert.Contains("location-operational-summary", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Location_detail_routes_each_physical_type_to_its_existing_editor()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Details.cshtml"));

        Assert.Contains("Model.Location.Kind == WarehouseEPI.Core.Entities.LocationKind.Rack", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"Rack/Edit\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"Area\" asp-route-locationId=\"@Model.Location.Id\"", page, StringComparison.Ordinal);
        Assert.Contains(">Editar área</a>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Area_editor_exposes_guarded_permanent_deletion()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Area.cshtml"));
        var model = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Area.cshtml.cs"));
        var program = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Program.cs"));

        Assert.Contains("Model.Deletion.CanDelete", page, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Delete\"", page, StringComparison.Ordinal);
        Assert.Contains("DeleteInput.ConfirmationCode", page, StringComparison.Ordinal);
        Assert.Contains("DeleteInput.Pin", page, StringComparison.Ordinal);
        Assert.Contains("OnPostDeleteAsync", model, StringComparison.Ordinal);
        Assert.Contains("LocationAreaDeleteCommand", model, StringComparison.Ordinal);
        Assert.Contains("AddScoped<LocationAreaAdministrationService>()", program, StringComparison.Ordinal);
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
