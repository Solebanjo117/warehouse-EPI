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
        Assert.Contains("existencia real", page, StringComparison.Ordinal);
        Assert.Contains("data-wip-destination-step", page, StringComparison.Ordinal);
        Assert.Contains("WipLocations", script, StringComparison.Ordinal);
        Assert.Contains("clearSelection(\"destination\")", script, StringComparison.Ordinal);
        Assert.Contains("item.isWip === true", script, StringComparison.Ordinal);
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
    public void Process_wip_exposes_three_balance_based_actions_review_camera_and_antiforgery_form()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "WipProcess.cshtml"));
        var model = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "WipProcess.cshtml.cs"));
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "wip-process.js"));

        Assert.Contains("Consumo", page, StringComparison.Ordinal);
        Assert.Contains("Regreso a bodega", page, StringComparison.Ordinal);
        Assert.Contains("Devolución a proveedor", page, StringComparison.Ordinal);
        Assert.Contains("data-wip-review", page, StringComparison.Ordinal);
        Assert.Contains("data-wip-camera", page, StringComparison.Ordinal);
        Assert.Contains("data-lookup-url", page, StringComparison.Ordinal);
        Assert.Contains("data-wip-product-search", page, StringComparison.Ordinal);
        Assert.Contains("data-wip-product-results", page, StringComparison.Ordinal);
        Assert.Contains("class=\"lookup-field\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"wip-camera-scanner-title\"", page, StringComparison.Ordinal);
        Assert.Contains("data-camera-video", page, StringComparison.Ordinal);
        Assert.Contains("data-camera-photo", page, StringComparison.Ordinal);
        Assert.Contains("data-camera-switch", page, StringComparison.Ordinal);
        Assert.Contains("zxing-browser.min.js", page, StringComparison.Ordinal);
        Assert.Contains("WipConsumption", model, StringComparison.Ordinal);
        Assert.Contains("WipSupplierReturn", model, StringComparison.Ordinal);
        Assert.Contains("Source.Id", model, StringComparison.Ordinal);
        Assert.Contains("handler: \"Products\"", script, StringComparison.Ordinal);
        Assert.Contains("Selecciona un producto de la lista.", script, StringComparison.Ordinal);
        Assert.Contains("handler: \"ResolveCode\"", script, StringComparison.Ordinal);
        Assert.Contains("scannerModal.show()", script, StringComparison.Ordinal);
        Assert.Contains("navigator.mediaDevices.getUserMedia", script, StringComparison.Ordinal);
        Assert.Contains("stream.getTracks().forEach(track => track.stop())", script, StringComparison.Ordinal);
        Assert.Contains("BarcodeDetector", script, StringComparison.Ordinal);
        Assert.Contains("event.submitter", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Wip_migration_only_expands_movement_constraints_without_creating_balance_data()
    {
        var migration = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Infrastructure", "Persistence", "Migrations", "20260828120000_WipTrackedInventory.cs"));

        Assert.Contains("WIP_CONSUMPTION", migration, StringComparison.Ordinal);
        Assert.Contains("WIP_SUPPLIER_RETURN", migration, StringComparison.Ordinal);
        Assert.Contains("'ENTRY', 'EXIT', 'TRANSFER'", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTable", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("InsertData", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory_balances\"", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_wip_detail_is_read_only_and_old_return_route_redirects_to_process_wip()
    {
        var detail = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Reports", "Wip", "Details.cshtml"));
        var returnPage = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "WipReturn.cshtml"));
        var returnModel = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Operations", "WipReturn.cshtml.cs"));

        Assert.Contains("Historial legado de solo lectura", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Registrar devolución", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.OriginalMovementLineId", returnPage, StringComparison.Ordinal);
        Assert.Contains("/Operations/WipProcess", returnModel, StringComparison.Ordinal);
        Assert.Contains("action = \"return\"", returnModel, StringComparison.Ordinal);
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
