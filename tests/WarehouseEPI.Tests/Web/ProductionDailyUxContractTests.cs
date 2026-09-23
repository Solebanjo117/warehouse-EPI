namespace WarehouseEPI.Tests.Web;

public sealed class ProductionDailyUxContractTests
{
    [Fact]
    public void Daily_center_reuses_product_lookup_and_exposes_advanced_fallback()
    {
        var root = FindRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Index.cshtml"));
        var script = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js", "production-daily.js"));
        Assert.Contains("data-product-url", page, StringComparison.Ordinal);
        Assert.Contains("_CaptureGroup", page, StringComparison.Ordinal);
        var capture = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "_CaptureGroup.cshtml"));
        Assert.Contains("GroupPreview", capture, StringComparison.Ordinal);
        Assert.Contains("Producción avanzada", page, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Enter\"", script, StringComparison.Ordinal);
        Assert.Contains("min-height:44px", File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "css", "production-daily.css")), StringComparison.Ordinal);
    }

    [Fact]
    public void Weekly_program_and_import_are_admin_pages()
    {
        var root = FindRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Production", "Schedule.cshtml"));
        var import = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Production", "ScheduleImport.cshtml"));
        Assert.Contains("Abrir semana para capturar", program, StringComparison.Ordinal);
        Assert.Contains("Orden 3", program, StringComparison.Ordinal);
        Assert.Contains("/Admin/Production/Routes", program, StringComparison.Ordinal);
        Assert.Contains("No crea inventario ni consumos históricos", import, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "src"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
