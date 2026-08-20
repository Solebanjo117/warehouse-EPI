namespace WarehouseEPI.Tests.Web;

public sealed class OperationLocationSelectionContractTests
{
    [Fact]
    public void Product_location_autoselection_excludes_entries_and_requires_one_option()
    {
        var script = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains(
            "operation !== \"entry\" && !selected[primaryLocationKind] && items.length === 1",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "await applySelection(primaryLocationKind, items[0], true)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "(item) => void applySelection(primaryLocationKind, item, true)",
            script,
            StringComparison.Ordinal);
    }

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
