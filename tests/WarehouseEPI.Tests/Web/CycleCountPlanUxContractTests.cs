using WarehouseEPI.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using WarehouseEPI.Web.Pages.Operations.CycleCounts;

namespace WarehouseEPI.Tests.Web;

public sealed class CycleCountPlanUxContractTests
{
    [Fact]
    public void Plan_form_uses_selectable_lookups_and_the_existing_cycle_camera()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "CycleCountPlans", "Index.cshtml");

        Assert.Contains("data-cycle-plan data-lookup-url=\"@Url.Page(\"/Operations/Lookup\")\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"Input.ProductId\" type=\"hidden\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"Input.LocationId\" type=\"hidden\"", page, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(page, "data-cycle-plan-search"));
        Assert.Equal(2, CountOccurrences(page, "data-cycle-plan-camera"));
        Assert.Equal(2, CountOccurrences(page, "role=\"listbox\""));
        Assert.Contains("aria-live=\"polite\"", page, StringComparison.Ordinal);
        Assert.Contains("/Pages/Operations/CycleCounts/_CameraScanner.cshtml", page, StringComparison.Ordinal);
        Assert.Contains("zxing-browser.min.js", page, StringComparison.Ordinal);
        Assert.Contains("~/js/cycle-count.js", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<select asp-for=\"Input.ProductId\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<select asp-for=\"Input.LocationId\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnumSelectList", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_lookup_supports_debounce_keyboard_exact_resolution_and_recoverable_camera_errors()
    {
        var script = Read("src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count.js");

        Assert.Contains("window.setTimeout(() => void search(field), 250)", script, StringComparison.Ordinal);
        Assert.Contains("handler = field.type === \"product\" ? \"Products\" : \"Locations\"", script, StringComparison.Ordinal);
        Assert.Contains("handler: \"ResolveCode\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"ArrowDown\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"ArrowUp\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Escape\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Enter\"", script, StringComparison.Ordinal);
        Assert.Contains("field.id.value = \"\"", script, StringComparison.Ordinal);
        Assert.Contains("se colocó en el campo correcto", script, StringComparison.Ordinal);
        Assert.Contains("return false;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_page_model_uses_warehouse_time_and_does_not_preload_catalogs()
    {
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Admin", "Inventory", "CycleCountPlans", "Index.cshtml.cs");

        Assert.Contains("OperationalInventoryQueryService operationalQuery", model, StringComparison.Ordinal);
        Assert.Contains("WarehouseClock warehouseClock", model, StringComparison.Ordinal);
        Assert.Contains("warehouseClock.GetDateAsync(timeProvider.GetUtcNow()", model, StringComparison.Ordinal);
        Assert.Contains("operationalQuery.GetProductAsync", model, StringComparison.Ordinal);
        Assert.Contains("operationalQuery.GetLocationAsync", model, StringComparison.Ordinal);
        Assert.Contains("ModelState.AddModelError(\"Input.ProductId\"", model, StringComparison.Ordinal);
        Assert.Contains("ModelState.AddModelError(\"Input.LocationId\"", model, StringComparison.Ordinal);
        Assert.DoesNotContain("dbContext.Products", model, StringComparison.Ordinal);
        Assert.DoesNotContain("dbContext.Locations", model, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime.Today", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Calendar_has_month_and_overdue_views_without_exposing_expected_quantity()
    {
        var page = Read("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts", "Calendar.cshtml");
        var model = Read("src", "WarehouseEPI.Web", "Pages", "Operations", "CycleCounts", "Calendar.cshtml.cs");

        Assert.Contains(">Mes</a>", page, StringComparison.Ordinal);
        Assert.Contains(">Vencidos</a>", page, StringComparison.Ordinal);
        Assert.Contains("item.ScheduledFor", page, StringComparison.Ordinal);
        Assert.Contains("item.CompletedQuantity", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedQuantity", page, StringComparison.Ordinal);
        Assert.Contains("DateOnly.TryParseExact", model, StringComparison.Ordinal);
        Assert.Contains("El mes indicado no es válido", model, StringComparison.Ordinal);
        Assert.Contains("timeProvider.GetUtcNow()", model, StringComparison.Ordinal);
    }

    [Fact]
    public void Administrative_plan_pages_are_protected_by_admin_policy()
    {
        foreach (var type in new[]
        {
            typeof(WarehouseEPI.Web.Pages.Admin.Inventory.CycleCountPlans.IndexModel),
            typeof(WarehouseEPI.Web.Pages.Admin.Inventory.CycleCountPlans.EditModel),
            typeof(WarehouseEPI.Web.Pages.Admin.Inventory.CycleCountPlans.HistoryModel)
        })
        {
            var authorize = Assert.Single(type.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
            Assert.Equal("AdminOnly", authorize.Policy);
        }
    }

    [Theory]
    [InlineData(CycleCountFrequency.Weekly, "Semanal")]
    [InlineData(CycleCountFrequency.Biweekly, "Quincenal")]
    [InlineData(CycleCountFrequency.Monthly, "Mensual")]
    [InlineData(CycleCountFrequency.Quarterly, "Trimestral")]
    [InlineData(CycleCountFrequency.Semiannual, "Semestral")]
    [InlineData(CycleCountFrequency.Annual, "Anual")]
    public void Every_frequency_has_a_spanish_label(CycleCountFrequency frequency, string expected)
    {
        Assert.Equal(expected, CycleCountPlanPresentation.FrequencyLabel(frequency));
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepositoryPath(parts));

    private static int CountOccurrences(string source, string value) =>
        (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string RepositoryPath(params string[] parts)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
                directory = directory.Parent;

            if (directory is not null)
                return Path.Combine([directory.FullName, .. parts]);
        }

        throw new DirectoryNotFoundException("No se encontró la raíz de Warehouse EPI.");
    }
}
