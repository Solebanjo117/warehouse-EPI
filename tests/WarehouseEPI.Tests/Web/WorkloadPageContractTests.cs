namespace WarehouseEPI.Tests.Web;

public sealed class WorkloadPageContractTests
{
    [Fact]
    public void Workload_page_keeps_pending_and_activity_separate_and_gates_admin_content()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Reports", "Workload", "Index.cshtml"));
        var model = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Reports", "Workload", "Index.cshtml.cs"));

        Assert.Contains("asp-route-view=\"pending\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"activity\"", page, StringComparison.Ordinal);
        Assert.Contains("Por atender", page, StringComparison.Ordinal);
        Assert.Contains("Actividad realizada", page, StringComparison.Ordinal);
        Assert.Contains("@if (queue.Exceptions is not null)", page, StringComparison.Ordinal);
        Assert.Contains("@if (Model.IsAdmin)", page, StringComparison.Ordinal);
        Assert.Contains("Comparación por responsable", page, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onchange=", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("UserId = null", model, StringComparison.Ordinal);
        Assert.Contains("Search = null", model, StringComparison.Ordinal);
        Assert.Contains("GetSnapshotAsync(new WorkQueueFilter(QueueSearch), IsAdmin", model, StringComparison.Ordinal);
        Assert.Contains("GetWorkloadPageAsync(filter, periodLabel, IsAdmin", model, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
