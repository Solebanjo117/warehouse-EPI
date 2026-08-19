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
