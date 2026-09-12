namespace WarehouseEPI.Tests.Web;

public sealed class ProductionWipDefaultsUxContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Product_and_process_editors_expose_accessible_external_script_contracts()
    {
        var product = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "_ProductionWipDefaults.cshtml"));
        var process = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Production", "ProcessEdit.cshtml"));
        var productScript = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "wwwroot", "js", "production-wip-defaults.js"));

        Assert.Contains("Destinos WIP de producción", product);
        Assert.Contains("data-material-wip-editor", product);
        Assert.Contains("role=\"combobox\"", product);
        Assert.Contains("aria-live=\"polite\"", product);
        Assert.Contains("WIP predeterminado del proceso", process);
        Assert.Contains("data-wip-default-picker", process);
        Assert.DoesNotContain("<script>", product, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AbortController", productScript);
        Assert.Contains("ArrowDown", productScript);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz.");
    }
}
