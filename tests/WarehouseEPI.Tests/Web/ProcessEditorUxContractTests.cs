namespace WarehouseEPI.Tests.Web;

public sealed class ProcessEditorUxContractTests
{
    [Fact]
    public void Process_editor_uses_one_accessible_multi_target_lookup()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Production", "ProcessEdit.cshtml");

        Assert.Contains("data-process-editor", page, StringComparison.Ordinal);
        Assert.Contains("data-target-lookup-url=\"@Url.Page(\"ProcessEdit\", \"WipTargets\")\"", page, StringComparison.Ordinal);
        Assert.Contains("role=\"combobox\"", page, StringComparison.Ordinal);
        Assert.Contains("role=\"listbox\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.Targets\"", page, StringComparison.Ordinal);
        Assert.Contains("data-process-target-remove", page, StringComparison.Ordinal);
        Assert.Contains("data-process-rack-reason", page, StringComparison.Ordinal);
        Assert.Contains("Áreas, filas o racks", page, StringComparison.Ordinal);
        Assert.Contains("data-process-original-grouped-target", page, StringComparison.Ordinal);
        Assert.Contains("~/js/process-editor.js", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<legend class=\"h5\">Áreas WIP", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<legend class=\"h5\">Racks WIP", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_target_lookup_supports_debounce_keyboard_multiple_selection_and_rack_reason()
    {
        var script = Read("src", "WarehouseEPI.Web", "wwwroot", "js", "process-editor.js");

        Assert.Contains("window.setTimeout(() => void search(), 250)", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"ArrowDown\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"ArrowUp\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Escape\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Enter\"", script, StringComparison.Ordinal);
        Assert.Contains("hidden.name = \"Input.Targets\"", script, StringComparison.Ordinal);
        Assert.Contains("selectedKeys().has(item.key)", script, StringComparison.Ordinal);
        Assert.Contains("data-process-original-grouped-target", script, StringComparison.Ordinal);
        Assert.Contains("item.type === \"row\"", script, StringComparison.Ordinal);
        Assert.Contains("selectedRows().has(rackRow(item.key))", script, StringComparison.Ordinal);
        Assert.Contains("Se retiraron ${removedRacks} racks individuales", script, StringComparison.Ordinal);
        Assert.Contains("updateReasonVisibility()", script, StringComparison.Ordinal);
        Assert.Contains("Selecciona un destino de los resultados", script, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepositoryPath(parts));

    private static string RepositoryPath(params string[] parts)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
                directory = directory.Parent;

            if (directory is not null)
                return Path.Combine([directory.FullName, .. parts]);
        }

        throw new DirectoryNotFoundException("No se encontró la raíz de Warehouse EPI.");
    }
}
