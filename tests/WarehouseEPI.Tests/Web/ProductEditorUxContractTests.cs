namespace WarehouseEPI.Tests.Web;

public sealed class ProductEditorUxContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Edit_exposes_accessible_tabs_compact_actions_and_location_filters()
    {
        var edit = Read("Pages", "Admin", "Catalogs", "Products", "Edit.cshtml");
        var form = Read("Pages", "Admin", "Catalogs", "Products", "_ProductForm.cshtml");
        var script = ReadScript("product-editor.js");

        Assert.Contains("data-product-tab=\"general\"", edit, StringComparison.Ordinal);
        Assert.Contains("data-product-tab=\"production\"", edit, StringComparison.Ordinal);
        Assert.Contains("data-product-tab=\"wip\"", edit, StringComparison.Ordinal);
        Assert.Contains("data-product-tab=\"locations\"", edit, StringComparison.Ordinal);
        Assert.Contains("assignedLocationSearch", edit, StringComparison.Ordinal);
        Assert.Contains("assignmentStatus", edit, StringComparison.Ordinal);
        Assert.Contains("AssignmentTotalPages", edit, StringComparison.Ordinal);
        Assert.Contains("data-product-actions", form, StringComparison.Ordinal);
        Assert.Contains("rows=\"2\"", form, StringComparison.Ordinal);
        Assert.Contains("beforeunload", script, StringComparison.Ordinal);
        Assert.Contains("Cambios pendientes", script, StringComparison.Ordinal);
        Assert.Contains("product-production-route", script, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", edit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_and_wip_use_progressive_disclosure_and_dynamic_rows()
    {
        var form = Read("Pages", "Admin", "Catalogs", "Products", "_ProductForm.cshtml");
        var wip = Read("Pages", "Admin", "Catalogs", "Products", "_ProductionWipDefaults.cshtml");
        var script = ReadScript("production-wip-defaults.js");

        Assert.Contains("<details", form, StringComparison.Ordinal);
        Assert.Contains("Destinos WIP opcionales", form, StringComparison.Ordinal);
        Assert.Contains("data-material-wip-template", wip, StringComparison.Ordinal);
        Assert.Contains("data-material-wip-add", wip, StringComparison.Ordinal);
        Assert.Contains("data-material-wip-remove", wip, StringComparison.Ordinal);
        Assert.Contains("Wip.Rules[${index}]", script, StringComparison.Ordinal);
        Assert.Contains("controller?.abort()", script, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, "src", "WarehouseEPI.Web", .. parts]));

    private static string ReadScript(string file) =>
        File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "wwwroot", "js", file));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz.");
    }
}
