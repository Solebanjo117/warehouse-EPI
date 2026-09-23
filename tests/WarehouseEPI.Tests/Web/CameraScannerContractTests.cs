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

        var wipPage = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "WipProcess.cshtml"));
        var wipScript = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "wip-process.js"));
        Assert.Contains("data-camera-switch", wipPage, StringComparison.Ordinal);
        Assert.Contains("warehouseEpi.preferredCameraDeviceId", wipScript, StringComparison.Ordinal);
        Assert.Contains("facingMode: { exact: \"environment\" }", wipScript, StringComparison.Ordinal);
        Assert.Contains("navigator.mediaDevices.enumerateDevices()", wipScript, StringComparison.Ordinal);
        Assert.Contains("if (error && !isCodeNotDetectedError(error))", wipScript, StringComparison.Ordinal);
        Assert.Contains("document.body.classList.add(\"camera-active\")", wipScript, StringComparison.Ordinal);
        Assert.Contains("else target?.focus()", wipScript, StringComparison.Ordinal);
        Assert.Contains("session !== cameraSession", wipScript, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"pagehide\"", wipScript, StringComparison.Ordinal);
        Assert.Contains("cameraSession++;", wipScript, StringComparison.Ordinal);
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

    [Fact]
    public void Cycle_counts_open_a_live_camera_and_keep_photo_as_a_fallback()
    {
        var directory = RepositoryDirectory("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts");
        var details = File.ReadAllText(Path.Combine(directory, "Details.cshtml"));
        var count = File.ReadAllText(Path.Combine(directory, "Count.cshtml"));
        var scanner = File.ReadAllText(Path.Combine(directory, "_CameraScanner.cshtml"));
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count.js"));

        Assert.Contains("data-cycle-scan-location", details, StringComparison.Ordinal);
        Assert.Contains("data-cycle-scan-product", count, StringComparison.Ordinal);
        Assert.Contains("_CameraScanner", details, StringComparison.Ordinal);
        Assert.Contains("_CameraScanner", count, StringComparison.Ordinal);
        Assert.Contains("data-cycle-camera-scanner", scanner, StringComparison.Ordinal);
        Assert.Contains("data-camera-video", scanner, StringComparison.Ordinal);
        Assert.Contains("data-camera-switch", scanner, StringComparison.Ordinal);
        Assert.Contains("data-camera-photo", scanner, StringComparison.Ordinal);

        Assert.Contains("navigator.mediaDevices.getUserMedia", script, StringComparison.Ordinal);
        Assert.Contains("facingMode: { exact: \"environment\" }", script, StringComparison.Ordinal);
        Assert.Contains("window.BarcodeDetector", script, StringComparison.Ordinal);
        Assert.Contains("decodeFromStream", script, StringComparison.Ordinal);
        Assert.Contains("if (error && !isCodeNotDetectedError(error))", script, StringComparison.Ordinal);
        Assert.Contains("stream.getTracks().forEach(track => track.stop())", script, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"pagehide\"", script, StringComparison.Ordinal);
        Assert.Contains("openCycleScanner(locationButton", script, StringComparison.Ordinal);
        Assert.Contains("openCycleScanner(productButton", script, StringComparison.Ordinal);
        Assert.Contains("openCycleScanner(scanButton", script, StringComparison.Ordinal);

        Assert.DoesNotContain("data-cycle-location-photo", details, StringComparison.Ordinal);
        Assert.DoesNotContain("data-cycle-scan-photo", count, StringComparison.Ordinal);
        Assert.DoesNotContain("locationButton.addEventListener(\"click\", () => photo.click())", script, StringComparison.Ordinal);
        Assert.DoesNotContain("productButton.addEventListener(\"click\", () => photo.click())", script, StringComparison.Ordinal);
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

    private static string RepositoryDirectory(params string[] directoryParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. directoryParts]);
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
