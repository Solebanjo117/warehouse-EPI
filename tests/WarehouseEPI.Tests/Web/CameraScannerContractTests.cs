namespace WarehouseEPI.Tests.Web;

public sealed class CameraScannerContractTests
{
    [Fact]
    public void Camera_scanners_allow_switching_devices_and_remember_the_selection()
    {
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains("facingMode: { exact: \"environment\" }", script, StringComparison.Ordinal);
        Assert.Contains("navigator.mediaDevices.enumerateDevices()", script, StringComparison.Ordinal);
        Assert.Contains("deviceId: { exact: preferredDeviceId }", script, StringComparison.Ordinal);
        Assert.Contains("warehouseEpi.preferredCameraDeviceId", script, StringComparison.Ordinal);

        Assert.Contains("data-camera-switch", File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "_GuidedMovementForm.cshtml")), StringComparison.Ordinal);
        Assert.Contains("data-camera-switch", File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Inventory", "Index.cshtml")), StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_lookup_announces_failures_and_restores_context()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Inventory", "Index.cshtml"));
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains("aria-labelledby=\"inventory-camera-scanner-title\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"inventory-search-feedback\"", page, StringComparison.Ordinal);
        Assert.Contains("data-inventory-highlighted", page, StringComparison.Ordinal);
        Assert.Contains("Escribe o escanea un producto o una ubicación.", script, StringComparison.Ordinal);
        Assert.Contains("No fue posible buscar en la red local.", script, StringComparison.Ordinal);
        Assert.Contains("highlighted.scrollIntoView", script, StringComparison.Ordinal);
        Assert.Contains("if (!resolving) input.focus()", script, StringComparison.Ordinal);
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
