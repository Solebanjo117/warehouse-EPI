namespace WarehouseEPI.Tests.Web;

public sealed class OperationLocationSelectionContractTests
{
    [Fact]
    public void Product_location_autoselection_uses_only_an_available_explicit_entry_default()
    {
        var script = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains(
            "operation === \"entry\" && !selected[primaryLocationKind] && selected.product.defaultEntryLocationId",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "selected.product.isDefaultEntryLocationAvailable && defaultLocation",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Destino principal aplicado: ${defaultLocation.code}. Puedes cambiarlo.",
            script,
            StringComparison.Ordinal);
        Assert.Contains("La ubicación principal ${code} no está disponible.", script, StringComparison.Ordinal);
        Assert.Contains("operation !== \"entry\" && !selected[primaryLocationKind] && items.length === 1", script, StringComparison.Ordinal);
        Assert.Contains("(item) => void applySelection(primaryLocationKind, item, true)", script, StringComparison.Ordinal);
        Assert.Contains("autoSelectedEntryDestinationForProductId === selected.product?.id", script, StringComparison.Ordinal);
        Assert.Contains("clearSelection(primaryLocationKind)", script, StringComparison.Ordinal);
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
