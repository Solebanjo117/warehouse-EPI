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

        Assert.Contains("var locationsIndexPage = isAdmin ? \"/Admin/Catalogs/Locations/Index\" : \"/Locations/Index\";", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"@locationsIndexPage\"", layout, StringComparison.Ordinal);
        Assert.Contains("IsSection(\"/Locations\") || (isAdmin && IsSection(\"/Admin/Catalogs/Locations\"))", layout, StringComparison.Ordinal);
        Assert.Equal(1, layout.Split("<span>Ubicaciones</span>", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("<span>Administrar ubicaciones</span>", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Locations/Details\"", inventory, StringComparison.Ordinal);
        Assert.Contains("value=\"unavailable\"", query, StringComparison.Ordinal);
        Assert.Contains("No disponibles", query, StringComparison.Ordinal);
        Assert.Contains("data-map-position-match", query, StringComparison.Ordinal);
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
