namespace WarehouseEPI.Tests.Web;

public sealed class ProductionMaterialUxContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Wip_issue_exposes_order_mode_lookup_and_external_script()
    {
        var form = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Operations", "_GuidedMovementForm.cshtml"));
        var exit = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Exit.cshtml"));
        var script = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "wwwroot", "js", "production-material-issue.js"));

        Assert.Contains("data-production-material-link", form);
        Assert.Contains("data-production-target-results", form);
        Assert.Contains("Input.WorkOrderStageId", form);
        Assert.Contains("production-material-issue.js", exit);
        Assert.Contains("setTimeout(load, 250)", script);
        Assert.Contains("event.key === \"Enter\"", script);
        Assert.Contains("event.key === \"Escape\"", script);
    }

    [Fact]
    public void Work_order_material_form_has_editable_lines_single_pin_summary_and_admin_reversal()
    {
        var page = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Work.cshtml"));
        var script = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "wwwroot", "js", "production-material-order.js"));

        Assert.Contains("Material.Lines[", page);
        Assert.Contains("Material.Pin", page);
        Assert.Contains("data-material-operation-form", page);
        Assert.Contains("ReverseMaterial", page);
        Assert.Contains("enteredQuantity", page);
        Assert.Contains("Model.Material.OperationId", page);
        Assert.Contains("window.confirm", script);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
