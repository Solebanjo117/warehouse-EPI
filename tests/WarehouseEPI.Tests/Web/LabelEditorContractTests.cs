namespace WarehouseEPI.Tests.Web;

public sealed class LabelEditorContractTests
{
    [Fact]
    public void Editor_exposes_powerpoint_style_commands_and_accessible_keyboard_contracts()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Labels", "Templates", "Edit.cshtml");
        var script = Read("src", "WarehouseEPI.Web", "wwwroot", "js", "label-template-editor.js");
        var program = Read("src", "WarehouseEPI.Web", "Program.cs");

        Assert.Contains("AuthorizeFolder(\"/Admin/Labels\", \"AdminOnly\")", program, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Lienzo de etiqueta", page, StringComparison.Ordinal);
        Assert.Contains("data-command=\"align-left\"", page, StringComparison.Ordinal);
        Assert.Contains("data-command=\"distribute-x\"", page, StringComparison.Ordinal);
        Assert.Contains("data-command=\"undo\"", page, StringComparison.Ordinal);
        Assert.Contains("data-snap", page, StringComparison.Ordinal);
        Assert.Contains("data-prop=\"rotation\"", page, StringComparison.Ordinal);
        Assert.Contains("event.key===\"Delete\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key===\"Escape\"", script, StringComparison.Ordinal);
        Assert.Contains("event.ctrlKey&&event.key.toLowerCase()===\"d\"", script, StringComparison.Ordinal);
        Assert.Contains("setPointerCapture", script, StringComparison.Ordinal);
        Assert.Contains("drag.resize", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_keeps_styles_server_controlled_and_prints_one_page_per_copy()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Operations", "Labels", "Index.cshtml");
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Operations", "Labels", "Index.cshtml.cs");
        var style = Read("src", "WarehouseEPI.Web", "wwwroot", "css", "label-4x6.css");

        Assert.Contains("Input.TemplateVersionId", page, StringComparison.Ordinal);
        Assert.DoesNotContain("DesignJson", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.Pin", page, StringComparison.Ordinal);
        Assert.Contains("templates.GetPublishedEntityAsync", model, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", model, StringComparison.Ordinal);
        Assert.DoesNotContain("InventoryMovement", model, StringComparison.Ordinal);
        Assert.Contains("data-label-copy", page, StringComparison.Ordinal);
        Assert.Contains("break-after: page", style, StringComparison.Ordinal);
        Assert.Contains("--label-width", style, StringComparison.Ordinal);
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
