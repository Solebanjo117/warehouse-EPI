using Microsoft.AspNetCore.Authorization;
using WarehouseEPI.Web.Pages.Reports.Executive;

namespace WarehouseEPI.Tests.Web;

public sealed class ExecutiveReportContractTests
{
    [Fact]
    public void Executive_page_is_admin_only_and_uses_external_assets()
    {
        var authorize = Assert.Single(typeof(IndexModel)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>());
        Assert.Equal("AdminOnly", authorize.Policy);

        var page = Read("src", "WarehouseEPI.Web", "Pages", "Reports", "Executive", "Index.cshtml");
        var css = Read("src", "WarehouseEPI.Web", "wwwroot", "css", "executive-report.css");
        var layout = Read("src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml");

        Assert.Contains("Movimientos efectivos registrados", page, StringComparison.Ordinal);
        Assert.Contains("Detalles de producto", page, StringComparison.Ordinal);
        Assert.Contains("Productos con más salidas", page, StringComparison.Ordinal);
        Assert.Contains("Productos con existencia y sin salida reciente", page, StringComparison.Ordinal);
        Assert.Contains("executive-report.css", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick=", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onchange=", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@media print", css, StringComparison.Ordinal);

        var adminBlock = layout.IndexOf("@if (isAdmin)", layout.IndexOf("Carga de trabajo", StringComparison.Ordinal), StringComparison.Ordinal);
        var executiveLink = layout.IndexOf("/Reports/Executive/Index", StringComparison.Ordinal);
        Assert.True(adminBlock >= 0 && executiveLink > adminBlock);
    }

    [Fact]
    public void Current_month_clamps_previous_period_to_available_day()
    {
        var period = IndexModel.ResolvePeriod(new DateOnly(2026, 3, 31), "this-month", null, null);

        Assert.Equal(new DateOnly(2026, 3, 1), period.From);
        Assert.Equal(new DateOnly(2026, 3, 31), period.To);
        Assert.Equal(new DateOnly(2026, 2, 1), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 2, 28), period.PreviousTo);
    }

    [Fact]
    public void Last_month_handles_year_boundary()
    {
        var period = IndexModel.ResolvePeriod(new DateOnly(2026, 1, 10), "last-month", null, null);

        Assert.Equal(new DateOnly(2025, 12, 1), period.From);
        Assert.Equal(new DateOnly(2025, 12, 31), period.To);
        Assert.Equal(new DateOnly(2025, 11, 1), period.PreviousFrom);
        Assert.Equal(new DateOnly(2025, 11, 30), period.PreviousTo);
    }

    [Theory]
    [InlineData("this-week", "2026-09-07", "2026-09-10", "2026-08-31", "2026-09-03")]
    [InlineData("last-week", "2026-08-31", "2026-09-06", "2026-08-24", "2026-08-30")]
    public void Week_periods_use_equivalent_calendar_days(
        string code, string from, string to, string previousFrom, string previousTo)
    {
        var period = IndexModel.ResolvePeriod(new DateOnly(2026, 9, 10), code, null, null);

        Assert.Equal(DateOnly.Parse(from), period.From);
        Assert.Equal(DateOnly.Parse(to), period.To);
        Assert.Equal(DateOnly.Parse(previousFrom), period.PreviousFrom);
        Assert.Equal(DateOnly.Parse(previousTo), period.PreviousTo);
    }

    [Fact]
    public void Custom_period_normalizes_inverted_dates_and_keeps_equal_day_count()
    {
        var period = IndexModel.ResolvePeriod(
            new DateOnly(2026, 9, 10), "custom", new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 8));

        Assert.Equal(new DateOnly(2026, 9, 8), period.From);
        Assert.Equal(new DateOnly(2026, 9, 10), period.To);
        Assert.Equal(new DateOnly(2026, 9, 5), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 9, 7), period.PreviousTo);
    }

    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory?.FullName ?? throw new DirectoryNotFoundException(), .. parts]));
    }
}
