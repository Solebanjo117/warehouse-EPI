namespace WarehouseEPI.Tests.Web;

public sealed class ProductionMaterialUxContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Exit_does_not_duplicate_order_linked_production_supply()
    {
        var form = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Operations", "_GuidedMovementForm.cshtml"));
        var exit = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Exit.cshtml"));

        Assert.DoesNotContain("data-production-material-link", form);
        Assert.DoesNotContain("data-production-target-results", form);
        Assert.DoesNotContain("Input.WorkOrderStageId", form);
        Assert.DoesNotContain("Para una orden", form);
        Assert.DoesNotContain("production-material-issue.js", exit);
    }

    [Fact]
    public void Work_order_material_form_has_editable_lines_single_pin_summary_and_admin_reversal()
    {
        var page = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Work.cshtml")) + File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "_WorkConsultation.cshtml"));
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
