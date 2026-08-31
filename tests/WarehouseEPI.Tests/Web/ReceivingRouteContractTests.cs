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
        Assert.Contains("/Operations/Receiving/Index", layout, StringComparison.Ordinal);
        Assert.Contains("/Admin/Inventory/Trace/Index", layout, StringComparison.Ordinal);
        Assert.Contains("Navegación temporalmente oculta: Recepciones contra documento", layout, StringComparison.Ordinal);
        Assert.Contains("Navegación temporalmente oculta: Trazabilidad unificada", layout, StringComparison.Ordinal);
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
