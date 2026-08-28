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
        Assert.Contains("width: var(--label-width); max-width: 100%", style, StringComparison.Ordinal);
        Assert.Contains("width: var(--label-width); max-width: none; height: var(--label-height)", style, StringComparison.Ordinal);
        Assert.Contains("@page { margin: 0; }", style, StringComparison.Ordinal);
        Assert.Contains(".app-body", style, StringComparison.Ordinal);
        Assert.Contains("min-height: 0 !important", style, StringComparison.Ordinal);
        Assert.Contains("break-inside: avoid-page", style, StringComparison.Ordinal);
        Assert.Contains("--barcode-print-width", style, StringComparison.Ordinal);
        Assert.Contains("data-label-barcode-dialog", page, StringComparison.Ordinal);
        Assert.Contains("data-label-barcode-zoom", page, StringComparison.Ordinal);
        Assert.DoesNotContain("calc(var(--label-width) * 10)", style, StringComparison.Ordinal);
    }

    [Fact]
    public void Pallet_label_print_button_is_registered_without_the_generic_product_workspace()
    {
        var palletPage = Read("src", "WarehouseEPI.Web", "Pages", "Operations", "PalletLabels", "Index.cshtml");
        var script = Read("src", "WarehouseEPI.Web", "wwwroot", "js", "label-4x6.js");

        Assert.Contains("data-label-print", palletPage, StringComparison.Ordinal);
        Assert.Contains("data-label-barcode-dialog", palletPage, StringComparison.Ordinal);
        Assert.Contains("data-label-barcode-zoom", palletPage, StringComparison.Ordinal);
        Assert.DoesNotContain("data-label-workspace", palletPage, StringComparison.Ordinal);
        Assert.Contains("document.querySelectorAll(\"[data-label-print]\")", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("document.querySelectorAll(\"[data-label-print]\")", StringComparison.Ordinal) <
            script.IndexOf("if (!workspace) return", StringComparison.Ordinal));
        Assert.Contains("barcodeDialog.showModal", script, StringComparison.Ordinal);
        Assert.Contains("modules * 3", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Pallet_label_page_offers_recent_entries_and_the_full_entry_detail_without_printing_them()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Operations", "PalletLabels", "Index.cshtml");
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Operations", "PalletLabels", "Index.cshtml.cs");
        var style = Read("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css");

        Assert.Contains("pallet-recent-list", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-id=\"@row.Candidate.MovementId\"", page, StringComparison.Ordinal);
        Assert.Contains("Model.Recent.Count == 0", page, StringComparison.Ordinal);
        Assert.Contains("Model.EntryLocalTime", page, StringComparison.Ordinal);
        Assert.Contains("Model.Entry.Responsible", page, StringComparison.Ordinal);
        Assert.Contains("Model.Entry.ExternalReference", page, StringComparison.Ordinal);
        Assert.Contains("plates.RecentAsync", model, StringComparison.Ordinal);
        Assert.Contains(".pallet-recent-item", style, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=", page, StringComparison.Ordinal);
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
