namespace WarehouseEPI.Tests.Web;

public sealed class WipExitFlowContractTests
{
    [Fact]
    public void Exit_flow_exposes_required_mode_and_a_whole_rack_wip_destination()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "_GuidedMovementForm.cshtml"));
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains("Input.ExitMode", page, StringComparison.Ordinal);
        Assert.Contains("Surtir WIP", page, StringComparison.Ordinal);
        Assert.Contains("Rack WIP — no controla saldo", page, StringComparison.Ordinal);
        Assert.Contains("data-wip-destination-step", page, StringComparison.Ordinal);
        Assert.Contains("WipLocations", script, StringComparison.Ordinal);
        Assert.Contains("clearSelection(\"destination\")", script, StringComparison.Ordinal);
        Assert.Contains("item.tracksInventory === false", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_wip_route_redirects_to_exit_wip_mode()
    {
        var pageModel = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "WipIssue.cshtml.cs"));

        Assert.Contains("/Operations/Exit", pageModel, StringComparison.Ordinal);
        Assert.Contains("mode = \"wip\"", pageModel, StringComparison.Ordinal);
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
