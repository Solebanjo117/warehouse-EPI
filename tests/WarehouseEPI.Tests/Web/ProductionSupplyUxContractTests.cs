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
    public void Linked_exit_carries_supply_concurrency_fields()
    {
        var root = FindRoot();
        var form = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations",
            "_GuidedMovementForm.cshtml"));
        var model = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations",
            "OperationPageModel.cs"));

        Assert.Contains("Input.SupplyRequestLineId", form, StringComparison.Ordinal);
        Assert.Contains("Input.ExpectedSupplyVersion", form, StringComparison.Ordinal);
        Assert.Contains("supplyRequestLineId", model, StringComparison.Ordinal);
        Assert.Contains("expectedSupplyVersion", model, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
