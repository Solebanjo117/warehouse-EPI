namespace WarehouseEPI.Tests.Web;

public sealed class ProductionReportContractTests
{
    [Fact]
    public void Report_page_preserves_admin_filters_exports_and_accessible_tables()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Reports", "Production", "Index.cshtml"));
        var model = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Reports", "Production", "Index.cshtml.cs"));
        var script = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js", "production-report.js"));

        Assert.Contains("[Authorize(Policy = \"AdminOnly\")]", model);
        Assert.Contains("[BindProperty(SupportsGet = true)]", model);
        Assert.Contains("asp-page-handler=\"Export\"", page);
        Assert.Contains("aria-label=\"@CatTexts[\"Vistas del reporte de producción\"]\"", page);
        Assert.Contains("data-production-print", page);
        Assert.DoesNotContain("onclick=", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window.print()", script);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró WarehouseEPI.sln.");
    }
}
