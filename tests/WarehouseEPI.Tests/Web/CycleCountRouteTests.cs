using WarehouseEPI.Core.Entities;
using WarehouseEPI.Web.Pages.Operations.CycleCounts;

namespace WarehouseEPI.Tests.Web;

public sealed class CycleCountRouteTests
{
    [Fact]
    public void Cycle_count_pages_are_public_and_all_inventory_changes_require_pin()
    {
        var directory = RepositoryDirectory("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts");
        var pageModels = Directory.GetFiles(directory, "*.cshtml.cs").Select(File.ReadAllText).ToArray();
        var details = File.ReadAllText(Path.Combine(directory, "Details.cshtml"));
        var count = File.ReadAllText(Path.Combine(directory, "Count.cshtml"));
        var review = File.ReadAllText(Path.Combine(directory, "Review.cshtml"));

        Assert.All(pageModels, content => Assert.DoesNotContain("[Authorize", content, StringComparison.Ordinal));
        Assert.Contains("type=\"password\"", details, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", count, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", review, StringComparison.Ordinal);
        Assert.Contains("OperationId", count, StringComparison.Ordinal);
        Assert.Contains("OperationId", review, StringComparison.Ordinal);
        Assert.Contains("UnexpectedEntries", count, StringComparison.Ordinal);
        Assert.Contains("cycle-count.js", count, StringComparison.Ordinal);
        Assert.Contains("SharedApprovals", review, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-antiforgery=\"false\"", string.Join('\n', Directory.GetFiles(directory, "*.cshtml").Select(File.ReadAllText)), StringComparison.Ordinal);
    }

    [Fact]
    public void Counting_and_print_views_remain_blind_until_review()
    {
        var directory = RepositoryDirectory("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts");
        var count = File.ReadAllText(Path.Combine(directory, "Count.cshtml"));
        var print = File.ReadAllText(Path.Combine(directory, "Print.cshtml"));
        var review = File.ReadAllText(Path.Combine(directory, "Review.cshtml"));

        Assert.Contains("Conteo ciego", count, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedQuantity", count, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedQuantity", print, StringComparison.Ordinal);
        Assert.DoesNotContain("Difference", print, StringComparison.Ordinal);
        Assert.Contains("ExpectedQuantity", review, StringComparison.Ordinal);
        Assert.Contains("Difference", review, StringComparison.Ordinal);
        Assert.Contains("@@media print", print, StringComparison.Ordinal);
        var script = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count.js"));
        Assert.Contains("ZXingBrowser", script, StringComparison.Ordinal);
        Assert.Contains("capture", script, StringComparison.Ordinal);
        Assert.Contains("lector HID", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_scope_selection_and_exports_are_connected()
    {
        var directory = RepositoryDirectory("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts");
        var create = File.ReadAllText(Path.Combine(directory, "Create.cshtml"));
        var createModel = File.ReadAllText(Path.Combine(directory, "Create.cshtml.cs"));
        var details = File.ReadAllText(Path.Combine(directory, "Details.cshtml"));
        var index = File.ReadAllText(Path.Combine(directory, "Index.cshtml"));
        var createScript = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count-create.js"));
        var export = File.ReadAllText(Path.Combine(directory, "Export.cshtml.cs"));
        var layout = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml"));
        var analytics = File.ReadAllText(RepositoryFile("src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml"));

        Assert.Contains("name=\"Input.LocationIds\"", create, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.RowCodes\"", create, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.RackNumbers\"", create, StringComparison.Ordinal);
        Assert.Contains("Input.RowCodes", createModel, StringComparison.Ordinal);
        Assert.Contains("Input.RackNumbers", createModel, StringComparison.Ordinal);
        Assert.Contains("data-cycle-row-group", create, StringComparison.Ordinal);
        Assert.Contains("data-cycle-rack-group", create, StringComparison.Ordinal);
        Assert.Contains("data-cycle-location", create, StringComparison.Ordinal);
        Assert.Contains("data-cycle-summary-groups", create, StringComparison.Ordinal);
        Assert.Contains("cycle-count-create.js", create, StringComparison.Ordinal);
        Assert.Contains("indeterminate", createScript, StringComparison.Ordinal);
        Assert.Contains("item.dataset.row === toggle.dataset.row && item.dataset.rack === toggle.dataset.rack", createScript, StringComparison.Ordinal);
        Assert.Contains("item.OperationalRole != LocationOperationalRole.Wip", createModel, StringComparison.Ordinal);
        Assert.DoesNotContain("item.TracksInventory", createModel, StringComparison.Ordinal);
        Assert.Contains("CampaignStatusLabel", index, StringComparison.Ordinal);
        Assert.Contains("LocationStatusLabel", details, StringComparison.Ordinal);
        Assert.Contains("ActiveAttemptId", details, StringComparison.Ordinal);
        Assert.Contains("Continuar conteo", details, StringComparison.Ordinal);
        Assert.Contains("d-lg-none", index, StringComparison.Ordinal);
        Assert.Contains("name=\"from\"", index, StringComparison.Ordinal);
        Assert.Contains("name=\"to\"", index, StringComparison.Ordinal);
        Assert.Contains("10000", export, StringComparison.Ordinal);
        Assert.Contains("ExportCycleCountsToExcelAsync", export, StringComparison.Ordinal);
        Assert.Contains("ExportCycleCountsToCsvAsync", export, StringComparison.Ordinal);
        Assert.Contains("/Operations/CycleCounts/Index", layout, StringComparison.Ordinal);
        Assert.Contains("/Operations/CycleCounts/Index", analytics, StringComparison.Ordinal);
    }

    [Fact]
    public void Operational_statuses_are_presented_in_spanish()
    {
        Assert.Equal("Borrador", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.Draft));
        Assert.Equal("Lista para contar", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.Released));
        Assert.Equal("En conteo", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.InProgress));
        Assert.Equal("Requiere revisión", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.UnderReview));
        Assert.Equal("Completada", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.Completed));
        Assert.Equal("Cancelada", CycleCountPresentation.CampaignStatusLabel(CycleCountCampaignStatus.Cancelled));
        Assert.Equal("Reconteo solicitado", CycleCountPresentation.LocationStatusLabel(CycleCountLocationStatus.RecountRequested));
        Assert.Equal("Saldo cambió", CycleCountPresentation.LocationStatusLabel(CycleCountLocationStatus.Stale));
    }

    private static string RepositoryFile(params string[] parts)
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

    private static string RepositoryDirectory(params string[] parts) =>
        Path.GetDirectoryName(RepositoryFile([.. parts, "Index.cshtml"]))!;
}
