namespace WarehouseEPI.Tests.Web;

public sealed class ReceivingRouteContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Receiving_routes_preserve_nip_antiforgery_and_deferred_navigation_contracts()
    {
        var program = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Program.cs"));
        var layout = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml"));
        var create = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Receiving", "New.cshtml"));
        var receive = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Receiving", "Receive.cshtml"));
        var trace = File.ReadAllText(Path.Combine(Root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "Trace", "Index.cshtml.cs"));
        Assert.Contains("ReceivingService", program, StringComparison.Ordinal);
        Assert.DoesNotContain(ModuleNavigationTestSupport.Actions(), action => action.Page == "/Operations/Receiving/Index");
        Assert.DoesNotContain(ModuleNavigationTestSupport.Actions(), action => action.Page == "/Admin/Inventory/Trace/Index");
        Assert.Contains("Input.Pin", create, StringComparison.Ordinal);
        Assert.Contains("Input.Pin", receive, StringComparison.Ordinal);
        Assert.Contains("[Authorize(Policy = \"AdminOnly\")]", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", create, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRoot()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"WarehouseEPI.sln")))directory=directory.Parent;
        return directory?.FullName??throw new DirectoryNotFoundException();
    }
}
