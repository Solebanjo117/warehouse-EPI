namespace WarehouseEPI.Tests.Web;

public sealed class LabelTemplateListContractTests
{
    [Fact]
    public void Template_list_groups_versions_by_template_instead_of_repeating_rows()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Labels", "Templates", "Index.cshtml");
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Labels", "Templates", "Index.cshtml.cs");

        // La lista recorre plantillas agrupadas, no versiones sueltas.
        Assert.Contains("foreach (var group in Model.Groups)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var row in Model.Rows)", page, StringComparison.Ordinal);
        Assert.Contains("GroupBy(row => row.TemplateId)", model, StringComparison.Ordinal);
        Assert.Contains("data-label-version=\"current\"", page, StringComparison.Ordinal);
        Assert.Contains("data-label-version=\"editable\"", page, StringComparison.Ordinal);

        // El número de versión se imprime; "v@row.Version" lo dejaba como texto literal.
        Assert.Contains("v@(current.Version)", page, StringComparison.Ordinal);
        Assert.Contains("v@(editable.Version)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("v@row.Version", page, StringComparison.Ordinal);

        // Solo puede existir una versión editable: la duplicación se ofrece cuando no la hay.
        Assert.Contains("group.Editable is null", page, StringComparison.Ordinal);

        // El historial y el retiro viven plegados, y los campos de retiro no repiten id.
        Assert.Contains("label-template-history", page, StringComparison.Ordinal);
        Assert.Contains("retire-reason-@group.TemplateId", page, StringComparison.Ordinal);
        Assert.Contains("retire-pin-@group.TemplateId", page, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-for=\"RetirePin\"", page, StringComparison.Ordinal);

        Assert.DoesNotContain("onclick=", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_cards_style_current_and_editable_versions_with_theme_tokens()
    {
        var style = Read("src", "WarehouseEPI.Web", "wwwroot", "css", "site.css");

        Assert.Contains(".label-version-row.is-current", style, StringComparison.Ordinal);
        Assert.Contains(".label-version-row.is-editable", style, StringComparison.Ordinal);
        Assert.Contains("var(--bs-success-bg-subtle)", style, StringComparison.Ordinal);
        Assert.Contains("var(--bs-warning-bg-subtle)", style, StringComparison.Ordinal);
        Assert.Contains(".label-version-actions .btn { min-height: 44px; }", style, StringComparison.Ordinal);
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
