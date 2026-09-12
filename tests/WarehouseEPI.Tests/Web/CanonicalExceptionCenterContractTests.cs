namespace WarehouseEPI.Tests.Web;

public sealed class CanonicalExceptionCenterContractTests
{
    [Fact]
    public void Admin_navigation_and_report_entry_points_use_the_exception_center()
    {
        var layout = Read("src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml");
        var dashboard = Read("src", "WarehouseEPI.Web", "Pages", "Reports", "Dashboard", "Index.cshtml");
        var inventory = Read("src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml");
        var workload = Read("src", "WarehouseEPI.Web", "Pages", "Reports", "Workload", "Index.cshtml");
        var executiveService = Read("src", "WarehouseEPI.Infrastructure", "Reporting", "ExecutiveReportService.cs");

        Assert.Contains("IsSection(\"/Admin/Inventory/Alerts\")", layout, StringComparison.Ordinal);
        Assert.Contains("<span>Centro de excepciones</span>", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<span>Alertas</span>", layout, StringComparison.Ordinal);

        Assert.Contains("asp-route-category=\"NegativeInventory\"", dashboard, StringComparison.Ordinal);
        Assert.Contains("asp-route-category=\"BelowMinimum\"", dashboard, StringComparison.Ordinal);
        Assert.Contains("asp-route-exception=\"negative\"", dashboard, StringComparison.Ordinal);
        Assert.Contains("asp-route-exception=\"minimum\"", dashboard, StringComparison.Ordinal);

        Assert.Contains("/Admin/Inventory/Alerts?category=BelowMinimum", executiveService, StringComparison.Ordinal);
        Assert.Contains("/Admin/Inventory/Alerts?category=StagnantInventory", executiveService, StringComparison.Ordinal);
        Assert.Contains("/Admin/Inventory/Alerts?category=NegativeInventory", executiveService, StringComparison.Ordinal);

        Assert.Contains("@if (isAdmin)", inventory, StringComparison.Ordinal);
        Assert.Contains("Esta consulta analítica se conserva para compatibilidad y exportación", inventory, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"exceptions\"", inventory, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Export\"", inventory, StringComparison.Ordinal);
        Assert.Contains("Abrir Centro de excepciones", workload, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_categories_and_legacy_bookmarks_remain_supported()
    {
        var alerts = Read("src", "WarehouseEPI.Infrastructure", "Reporting", "OperationalAlertService.cs");
        var pageModel = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Alerts.cshtml.cs");

        foreach (var category in new[]
                 {
                     "NegativeInventory", "BelowMinimum", "UnassignedBalance", "RestrictedInventory",
                     "StagnantInventory", "CycleCountStale", "CycleCountPending", "AgedWip"
                 })
        {
            Assert.Contains($"/Admin/Inventory/Alerts?category={category}", alerts, StringComparison.Ordinal);
        }

        foreach (var legacyView in new[] { "negative", "minimum", "unassigned", "restricted", "stagnant", "cycle", "wip" })
            Assert.Contains($"\"{legacyView}\"", pageModel, StringComparison.Ordinal);

        Assert.Contains("category ?? LegacyCategory(view, attention)", pageModel, StringComparison.Ordinal);
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
