namespace WarehouseEPI.Tests.Web;

public sealed class ProductionSupplyUxContractTests
{
    [Fact]
    public void Queue_preserves_touch_accessibility_polling_and_no_inline_scripts()
    {
        var root = FindRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations",
            "ProductionSupply", "Index.cshtml"));
        var script = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js",
            "production-supply-queue.js"));
        var layout = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml"));

        Assert.Contains("Surtimientos a producción", page, StringComparison.Ordinal);
        Assert.Contains("data-supply-queue", page, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("30000", script, StringComparison.Ordinal);
        Assert.Contains("data-supply-count-root", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_linked_exit_redirects_into_guided_preparation()
    {
        var root = FindRoot();
        var form = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations",
            "_GuidedMovementForm.cshtml"));
        var model = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations",
            "OperationPageModel.cs"));

        var exit = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations",
            "Exit.cshtml.cs"));
        Assert.DoesNotContain("Input.SupplyRequestLineId", form, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.ExpectedSupplyVersion", form, StringComparison.Ordinal);
        Assert.Contains("supplyRequestLineId", exit, StringComparison.Ordinal);
        Assert.Contains("ResolveLegacyLineAsync", exit, StringComparison.Ordinal);
        Assert.Contains("/Operations/ProductionSupply/Prepare", exit, StringComparison.Ordinal);
    }

    [Fact]
    public void Guided_preparation_preserves_accessibility_and_has_no_inline_script()
    {
        var root = FindRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations",
            "ProductionSupply", "Prepare.cshtml"));
        var script = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js",
            "production-supply-preparation.js"));

        Assert.Contains("data-supply-preparation", page, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", page, StringComparison.Ordinal);
        Assert.Contains("Confirmar surtimiento", page, StringComparison.Ordinal);
        Assert.Contains("Comprobante de surtimiento", page, StringComparison.Ordinal);
        Assert.Contains("Continuar pendiente", page, StringComparison.Ordinal);
        Assert.Contains("production-supply-preparation.js", page, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-source-quantity", script, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
