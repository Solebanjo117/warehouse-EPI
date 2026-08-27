namespace WarehouseEPI.Tests.Web;

public sealed class UnifiedMovementRouteTests
{
    [Fact]
    public void Canonical_movement_page_exposes_both_populations_shared_filters_and_detail_links()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Index.cshtml");
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Index.cshtml.cs");

        Assert.Contains("<h1 class=\"h2 mb-1\">Movimientos</h1>", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"effective\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"audit\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"movementType\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"purpose\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"sku\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"locationCode\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"state\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-returnUrl=\"@returnUrl\"", page, StringComparison.Ordinal);
        Assert.Contains("MovementReportService", model, StringComparison.Ordinal);
        Assert.Contains("InventoryHistoryService", model, StringComparison.Ordinal);
        Assert.Contains("movementType ?? type", model, StringComparison.Ordinal);
        Assert.Contains("GetTraceExportAsync", model, StringComparison.Ordinal);
        Assert.Contains("10000", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_has_one_movement_entry_and_legacy_route_redirects_with_export_compatibility()
    {
        var layout = Read("src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml");
        var legacy = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Reports", "Movements", "Index.cshtml.cs");

        Assert.Equal(1, Count(layout, "asp-page=\"/Admin/Inventory/Movements/Index\""));
        Assert.DoesNotContain("asp-page=\"/Admin/Reports/Movements/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("/Admin/Inventory/Movements", legacy, StringComparison.Ordinal);
        Assert.Contains("view=effective", legacy, StringComparison.Ordinal);
        Assert.Contains("OnGetExport() => OnGet()", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public void Movement_detail_validates_local_return_targets_and_preserves_them_through_correction_links()
    {
        var detail = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml");
        var detailModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml.cs");
        var correct = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Correct.cshtml");
        var correctModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Correct.cshtml.cs");

        Assert.Contains("Url.IsLocalUrl(returnUrl)", detailModel, StringComparison.Ordinal);
        Assert.Contains("href=\"@Model.ReturnUrl\"", detail, StringComparison.Ordinal);
        Assert.Contains("asp-route-returnUrl=\"@Model.ReturnUrl\"", detail, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"ReturnUrl\" type=\"hidden\"", correct, StringComparison.Ordinal);
        Assert.Contains("Url.IsLocalUrl(returnUrl)", correctModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_entry_movements_expose_a_pallet_plate_action_without_adding_it_to_audit_rows()
    {
        var index = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Index.cshtml");
        var indexModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Index.cshtml.cs");
        var detail = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml");
        var detailModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Movements", "Details.cshtml.cs");

        Assert.Contains("CanGeneratePalletPlate(item)", index, StringComparison.Ordinal);
        Assert.Equal(1, Count(index, "asp-page=\"/Operations/PalletLabels/Index\""));
        Assert.Contains("PalletLicensePlateService.IsEligible", indexModel, StringComparison.Ordinal);
        Assert.Contains("CanGeneratePalletPlate", detail, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Operations/PalletLabels/Index\"", detail, StringComparison.Ordinal);
        Assert.Contains("PalletLicensePlateService.IsEligible", detailModel, StringComparison.Ordinal);
    }

    private static int Count(string value, string search)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0; index += search.Length)
            count++;
        return count;
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepositoryPath(parts));

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
        return Path.Combine([directory.FullName, .. parts]);
    }
}
