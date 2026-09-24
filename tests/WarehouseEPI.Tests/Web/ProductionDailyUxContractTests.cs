namespace WarehouseEPI.Tests.Web;

public sealed class ProductionDailyUxContractTests
{
    [Fact]
    public void Balance_day_navigation_preserves_get_date_and_accessible_week_selection()
    {
        var page = File.ReadAllText(Path.Combine(FindRoot(), "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "_WeeklyBalance.cshtml"));
        Assert.DoesNotContain("data-balance-date", page, StringComparison.Ordinal);
        Assert.Contains("ProductionWeekCalendar.Days(weekly.WeekStart)", page, StringComparison.Ordinal);
        Assert.Contains("data-balance-day=", page, StringComparison.Ordinal);
        Assert.Contains("aria-current=", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Through\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-Through=", page, StringComparison.Ordinal);
        Assert.Contains("overflow-auto", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Balance_shows_status_after_ready_to_pack_and_collapsible_weekly_shift_comparison()
    {
        var root = FindRoot();
        var balance = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "_WeeklyBalance.cshtml"));
        var close = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "_WeekClose.cshtml"));
        Assert.Contains("data-balance-status", balance, StringComparison.Ordinal);
        Assert.Contains("product.StatusRatio", balance, StringComparison.Ordinal);
        Assert.Contains("shift-comparison-heading", close, StringComparison.Ordinal);
        Assert.Contains("close.ShiftComparison", close, StringComparison.Ordinal);
    }


    [Fact]
    public void Daily_center_reuses_product_lookup_without_advanced_navigation()
    {
        var root = FindRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Index.cshtml"));
        var script = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js", "production-daily.js"));
        Assert.Contains("data-product-url", page, StringComparison.Ordinal);
        Assert.Contains("_CaptureGroup", page, StringComparison.Ordinal);
        var capture = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "_CaptureGroup.cshtml"));
        Assert.Contains("GroupPreview", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page=\"Advanced\"", page, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Enter\"", script, StringComparison.Ordinal);
        Assert.Contains("min-height:44px", File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "css", "production-daily.css")), StringComparison.Ordinal);
    }

    [Fact]
    public void Weekly_program_and_import_are_admin_pages()
    {
        var root = FindRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Production", "Schedule.cshtml"));
        var import = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Production", "ScheduleImport.cshtml"));
        Assert.Contains("Abrir semana para capturar", program, StringComparison.Ordinal);
        Assert.Contains("Orden 3", program, StringComparison.Ordinal);
        Assert.Contains("/Admin/Production/Routes", program, StringComparison.Ordinal);
        Assert.Contains("No crea inventario ni consumos históricos", import, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "src"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
