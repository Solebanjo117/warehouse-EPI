namespace WarehouseEPI.Tests.Web;

using WarehouseEPI.Web.Pages.Operations;

public sealed class WipExitFlowContractTests
{
    [Fact]
    public void Exit_flow_exposes_required_mode_and_a_whole_rack_wip_destination()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "_GuidedMovementForm.cshtml"));
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "operations.js"));

        Assert.Contains("Input.ExitMode", page, StringComparison.Ordinal);
        Assert.Contains("data-edit-step=\"exit-mode\"", page, StringComparison.Ordinal);
        Assert.Contains("Surtir WIP", page, StringComparison.Ordinal);
        Assert.Contains("Rack WIP — no controla saldo", page, StringComparison.Ordinal);
        Assert.Contains("data-wip-destination-step", page, StringComparison.Ordinal);
        Assert.Contains("WipLocations", script, StringComparison.Ordinal);
        Assert.Contains("clearSelection(\"destination\")", script, StringComparison.Ordinal);
        Assert.Contains("item.tracksInventory === false", script, StringComparison.Ordinal);
        Assert.Contains("kind === \"exit-mode\"", script, StringComparison.Ordinal);
        Assert.Contains("exitModePicker?.querySelector(\"input:checked\")", script, StringComparison.Ordinal);
        Assert.Contains("const refreshExitMode", script, StringComparison.Ordinal);
        Assert.Contains("exitModePicker?.addEventListener(\"change\", refreshExitMode)", script, StringComparison.Ordinal);
        Assert.Contains("visibleGuidedKinds().find(kind => kind === \"exit-mode\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("invalid", null)]
    [InlineData("general", ExitMode.General)]
    [InlineData("GENERAL", ExitMode.General)]
    [InlineData("wip", ExitMode.Wip)]
    [InlineData("WIP", ExitMode.Wip)]
    public async Task Exit_only_prefills_an_explicit_valid_mode(string? mode, ExitMode? expected)
    {
        var pageModel = new ExitModel(null!, null!, null!);

        await pageModel.OnGetAsync(null, null, null, null, mode, CancellationToken.None);

        Assert.Equal(expected, pageModel.Input.ExitMode);
    }

    [Fact]
    public void Legacy_wip_route_redirects_to_exit_wip_mode()
    {
        var pageModel = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "WipIssue.cshtml.cs"));

        Assert.Contains("/Operations/Exit", pageModel, StringComparison.Ordinal);
        Assert.Contains("mode = \"wip\"", pageModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Wip_detail_starts_a_prefilled_return_only_when_quantity_remains()
    {
        var detail = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Reports", "Wip", "Details.cshtml"));
        var returnPage = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "WipReturn.cshtml"));
        var returnModel = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "WipReturn.cshtml.cs"));

        Assert.Contains("Model.Issue.Returnable > 0", detail, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Operations/WipReturn\"", detail, StringComparison.Ordinal);
        Assert.Contains("asp-route-lineId=\"@Model.Issue.MovementLineId\"", detail, StringComparison.Ordinal);
        Assert.Contains("Registrar devolución", detail, StringComparison.Ordinal);
        Assert.Contains("Surtimiento completamente procesado", detail, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"Input.OriginalMovementLineId\"", returnPage, StringComparison.Ordinal);
        Assert.Contains("disponible @issue.Returnable", returnPage, StringComparison.Ordinal);
        Assert.Contains("Input.OperationId = Guid.NewGuid()", returnModel, StringComparison.Ordinal);
        Assert.Contains("Input.OriginalMovementLineId = selected", returnModel, StringComparison.Ordinal);
        Assert.Contains("SelectedIssue = await reportService.GetIssueAsync(selected", returnModel, StringComparison.Ordinal);
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
