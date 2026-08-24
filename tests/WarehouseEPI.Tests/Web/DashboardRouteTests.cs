using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Reports.Dashboard;

namespace WarehouseEPI.Tests.Web;

public sealed class DashboardRouteTests
{
    [Fact]
    public async Task Metrics_handler_returns_snapshot_and_disables_http_caching()
    {
        await using var db = CreateDbContext();
        var model = new IndexModel(
            new DailyDashboardService(db, new WarehouseSettingsService(db)),
            new MemoryCache(Options.Create(new MemoryCacheOptions())),
            TimeProvider.System)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await model.OnGetMetricsAsync(false, CancellationToken.None);

        var json = Assert.IsType<JsonResult>(result);
        var snapshot = Assert.IsType<DailyDashboardSnapshotDto>(json.Value);
        Assert.Equal(14, snapshot.Metrics.RecentActivityTrend.Count);
        Assert.Contains("no-store", model.Response.Headers.CacheControl.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Metrics_handler_uses_cache_for_polling_and_bypasses_it_for_manual_refresh()
    {
        await using var db = CreateDbContext();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var model = new IndexModel(
            new DailyDashboardService(db, new WarehouseSettingsService(db)),
            new MemoryCache(Options.Create(new MemoryCacheOptions())),
            time)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        var first = Assert.IsType<DailyDashboardSnapshotDto>(
            Assert.IsType<JsonResult>(await model.OnGetMetricsAsync(false)).Value);
        time.Advance(TimeSpan.FromMinutes(5));
        var cached = Assert.IsType<DailyDashboardSnapshotDto>(
            Assert.IsType<JsonResult>(await model.OnGetMetricsAsync(false)).Value);
        var refreshed = Assert.IsType<DailyDashboardSnapshotDto>(
            Assert.IsType<JsonResult>(await model.OnGetMetricsAsync(true)).Value);

        Assert.Equal(first.GeneratedAtLocal, cached.GeneratedAtLocal);
        Assert.True(refreshed.GeneratedAtLocal > cached.GeneratedAtLocal);
    }

    [Fact]
    public void Dashboard_is_public_server_rendered_and_contextual_links_require_admin()
    {
        var page = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Dashboard", "Index.cshtml"));
        var pageModel = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Dashboard", "Index.cshtml.cs"));

        Assert.Contains("@page \"/Reports/Dashboard\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorize", pageModel, StringComparison.Ordinal);
        Assert.Contains("Tablero diario", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-chart", page, StringComparison.Ordinal);
        Assert.Contains("<canvas", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-fallback", page, StringComparison.Ordinal);
        Assert.Contains("/lib/chart.js/chart.umd.min.js", page, StringComparison.Ordinal);
        Assert.DoesNotContain("cdnjs", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jsdelivr", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-dashboard-range=\"14\"", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-range=\"7\"", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-summary", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-detail", page, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"dashboard-chart-detail\"", page, StringComparison.Ordinal);
        Assert.Contains("data-warehouse-date", page, StringComparison.Ordinal);
        Assert.Contains("/js/daily-dashboard.js", page, StringComparison.Ordinal);
        Assert.Contains("User.IsInRole(\"ADMIN\")", page, StringComparison.Ordinal);
        Assert.Contains("if (isAdmin)", page, StringComparison.Ordinal);
        Assert.Contains("/Admin/Inventory/Movements/Index", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"effective\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/Admin/Reports/Movements/Index", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"exceptions\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-exception=\"negative\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-exception=\"minimum\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_script_contains_polling_visibility_and_stale_data_contracts()
    {
        var script = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "wwwroot", "js", "daily-dashboard.js"));

        Assert.Contains("intervalMilliseconds = 60000", script, StringComparison.Ordinal);
        Assert.Contains("requestInProgress", script, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", script, StringComparison.Ordinal);
        Assert.Contains("Datos sin actualizar", script, StringComparison.Ordinal);
        Assert.Contains("Actualizando datos", script, StringComparison.Ordinal);
        Assert.Contains("cache: \"no-store\"", script, StringComparison.Ordinal);
        Assert.Contains("searchParams.set(\"refresh\", \"true\")", script, StringComparison.Ordinal);
        Assert.Contains("() => refresh(true)", script, StringComparison.Ordinal);
        Assert.Contains("selectedRange = 14", script, StringComparison.Ordinal);
        Assert.Contains("new Chart", script, StringComparison.Ordinal);
        Assert.Contains("shell.classList.add(\"is-ready\")", script, StringComparison.Ordinal);
        Assert.Contains("fallback.hidden = true", script, StringComparison.Ordinal);
        Assert.Contains("stacked: true", script, StringComparison.Ordinal);
        Assert.Contains("chart.update(\"none\")", script, StringComparison.Ordinal);
        Assert.Contains("tooltip", script, StringComparison.Ordinal);
        Assert.Contains("dashboardColumnHighlight", script, StringComparison.Ordinal);
        Assert.Contains("dashboardStackTotals", script, StringComparison.Ordinal);
        Assert.Contains("maxBarThickness: 42", script, StringComparison.Ordinal);
        Assert.Contains("cornerRadius: 10", script, StringComparison.Ordinal);
        Assert.Contains("ArrowLeft", script, StringComparison.Ordinal);
        Assert.Contains("aria-busy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);

        var chart = RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "lib", "chart.js", "chart.umd.min.js");
        var license = RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "lib", "chart.js", "LICENSE.md");
        Assert.True(File.Exists(chart));
        Assert.Contains("The MIT License", File.ReadAllText(license), StringComparison.Ordinal);
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

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"DashboardRouteTests-{Guid.NewGuid():N}")
            .Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan interval) => current = current.Add(interval);
    }
}
