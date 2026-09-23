namespace WarehouseEPI.Tests.Web;

public sealed class WarehouseMapCalibrationContractTests
{
    [Fact]
    public void Calibration_and_query_expose_separate_admin_and_operator_contracts()
    {
        var calibration = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Locations", "Map", "Calibration.cshtml");
        var query = Read("src", "WarehouseEPI.Web", "Pages", "Locations", "_LocationIndex.cshtml");
        var program = Read("src", "WarehouseEPI.Web", "Program.cs");
        var tracker = Read("src", "WarehouseEPI.Web", "wwwroot", "js", "warehouse-location.js");

        Assert.Contains("data-calibration-editor", calibration, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Publish\"", calibration, StringComparison.Ordinal);
        Assert.Contains("NIP ADMIN", calibration, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", calibration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-location-start>@CatTexts[\"Mi ubicación\"]", query, StringComparison.Ordinal);
        Assert.Contains("data-location-accuracy", query, StringComparison.Ordinal);
        Assert.Contains("geolocation={(allowsGeolocation ? \"(self)\" : \"()\")}", program, StringComparison.Ordinal);
        Assert.Contains("watchPosition", tracker, StringComparison.Ordinal);
        Assert.Contains("enableHighAccuracy: true", tracker, StringComparison.Ordinal);
        Assert.Contains("maximumAge: 0", tracker, StringComparison.Ordinal);
        Assert.Contains("timeout: 20000", tracker, StringComparison.Ordinal);
        Assert.DoesNotContain("method: \"POST\"", tracker, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(RepositoryRoot(), Path.Combine(path)));
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
