namespace WarehouseEPI.Tests.Web;

public sealed class ProductionPlanningUxContractTests
{
    [Fact]
    public void Planning_ui_keeps_accessibility_keyboard_and_external_script_contracts()
    {
        var root = FindRoot();
        var partial = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "_Planning.cshtml"));
        var page = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Work.cshtml"));
        var script = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js", "production-planning.js"));
        Assert.Contains("Planificación de materiales", partial);
        Assert.Contains("no reservan ni mueven inventario", partial);
        Assert.Contains("Cambiar para esta orden", partial);
        Assert.Contains("Estado", partial);
        Assert.Contains("role=\"combobox\"", partial);
        Assert.Contains("aria-live=\"polite\"", partial);
        Assert.Contains("User.IsInRole(\"ADMIN\")", partial);
        Assert.Contains("!User.IsInRole(\"ADMIN\")", File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Work.cshtml.cs")));
        Assert.DoesNotContain("<script", partial, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("production-planning.js", page);
        Assert.Contains("ArrowDown", script);
        Assert.Contains("Enter", script);
        Assert.Contains("Escape", script);
        Assert.Contains("AbortController", script);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
