using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Reports.Dashboard;

namespace WarehouseEPI.Tests.Web;

public sealed class DashboardRouteTests
{
    [Theory]
    [InlineData("es", 3)]
    [InlineData("en", 3)]
    [InlineData("es", 0)]
    [InlineData("en", 0)]
    public async Task Initial_chart_points_match_metrics_json_without_refresh(string language, int entries)
    {
        using var factory = new AdminRouteTests.WarehouseApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var admin = new User { FullName = "Dashboard test", RoleId = 1, PinHash = "", PinLookup = "" };
        await scope.ServiceProvider.GetRequiredService<UserPinService>().AssignAsync(admin, "0123");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/Admin/Login");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var signedIn = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Pin"] = "0123",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
        }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

        var date = new DateOnly(2026, 10, 7);
        var points = Enumerable.Range(0, 14).Select(index => new MovementActivityPointDto(
            date.AddDays(index - 13), $"Day {index + 1}", entries, entries * 2, entries * 3,
            entries * 4, entries * 10, entries)).ToArray();
        factory.Services.GetRequiredService<IMemoryCache>().Set("reporting:daily-dashboard:14-days",
            new DailyDashboardSnapshotDto(date, new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero),
                new(entries * 10, 0, 0, entries * 4, points)));

        var cache = factory.Services.GetRequiredService<IMemoryCache>();
        var generated = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var calendarPoints = Enumerable.Range(0, 90).Select(index => points[0] with { Date = date.AddDays(index - 89) }).ToArray();
        cache.Set("reporting:dashboard-activity:90", new DashboardActivityDto(date, generated, 90, calendarPoints));
        foreach (var days in new[] { 7, 14, 90 })
        {
            foreach (var page in new[] { 1, 2 })
            {
                var items = entries == 0 ? [] : Enumerable.Range((page - 1) * 10, page == 1 ? 10 : 2)
                    .Select(index => new DashboardProductDto(Guid.NewGuid(), $"DASH-{index:00}", "Product <safe>",
                        entries * 10, 10m, entries, entries * 2, entries * 3, entries * 4,
                        [new DashboardLocationDto("RACK-1", entries)])).ToArray();
                cache.Set($"reporting:dashboard-products:{days}:{page}", new DashboardProductsDto(date.AddDays(1 - days), date,
                    generated, days, page, entries == 0 ? 0 : 12, entries * 10 * days, items));
            }
        }

        client.DefaultRequestHeaders.Add("Cookie", $"WarehouseEPI.Language={language}");
        var html = await client.GetStringAsync("/Reports/Dashboard");
        Assert.Contains($"<html lang=\"{language}\"", html, StringComparison.Ordinal);
        var attribute = Regex.Match(html, "data-points=\"([^\"]+)\"");
        Assert.True(attribute.Success);
        using var initial = JsonDocument.Parse(WebUtility.HtmlDecode(attribute.Groups[1].Value));
        using var refreshed = JsonDocument.Parse(await client.GetStringAsync("/Reports/Dashboard?handler=Metrics"));
        var trend = refreshed.RootElement.GetProperty("metrics").GetProperty("recentActivityTrend");
        Assert.Equal(14, initial.RootElement.GetArrayLength());
        for (var index = 0; index < 14; index++)
        {
            foreach (var key in new[] { "date", "dayLabel", "entryCount", "exitCount", "transferCount",
                "adjustmentCount", "totalEffectiveOperations", "distinctSkusCount" })
                Assert.Equal(trend[index].GetProperty(key).ToString(), initial.RootElement[index].GetProperty(key).ToString());
            Assert.Equal(entries, initial.RootElement[index].GetProperty("entryCount").GetInt32());
            Assert.Equal(entries * 10, initial.RootElement[index].GetProperty("totalEffectiveOperations").GetInt32());
        }

        var output = Environment.GetEnvironmentVariable("WAREHOUSE_DASHBOARD_FIXTURES");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"dashboard-{language}-{entries}.html"),
                Regex.Replace(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>", ""));
            await File.WriteAllTextAsync(Path.Combine(output, $"metrics-{entries}.json"), refreshed.RootElement.GetRawText());
            await File.WriteAllTextAsync(Path.Combine(output, $"activity-{entries}.json"),
                await client.GetStringAsync("/Reports/Dashboard?handler=Activity&days=90"));
            foreach (var days in new[] { 7, 14, 90 })
                foreach (var page in new[] { 1, 2 })
                    await File.WriteAllTextAsync(Path.Combine(output, $"products-{entries}-{days}-{page}.json"),
                        await client.GetStringAsync($"/Reports/Dashboard?handler=Products&days={days}&pageNumber={page}"));
        }
        foreach (var handler in new[] { "Activity", "Products" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/Reports/Dashboard?handler={handler}&days=365")).StatusCode);
            using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync($"/Reports/Dashboard?handler={handler}")).StatusCode);
            var refreshedResponse = await client.GetAsync($"/Reports/Dashboard?handler={handler}&refresh=true");
            Assert.Equal(HttpStatusCode.OK, refreshedResponse.StatusCode);
            Assert.Contains("no-store", refreshedResponse.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        }
    }

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
        Assert.True(
            page.IndexOf("data-dashboard-range=\"7\"", StringComparison.Ordinal)
            < page.IndexOf("data-dashboard-range=\"14\"", StringComparison.Ordinal));
        Assert.Contains("Actividad del almacén", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-period-label", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-summary", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-detail", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-detail-empty", page, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-detail-link", page, StringComparison.Ordinal);
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
        Assert.Contains("maxBarThickness: 36", script, StringComparison.Ordinal);
        Assert.Contains("getValueForPixel", script, StringComparison.Ordinal);
        Assert.Contains("Sin actividad en el período", script, StringComparison.Ordinal);
        Assert.Contains("[text(point.dayLabel), translate(\"Hoy\")]", script, StringComparison.Ordinal);
        Assert.Contains("chart.tooltip.setActiveElements([]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("context.fillRect", script, StringComparison.Ordinal);
        Assert.Contains("cornerRadius: 10", script, StringComparison.Ordinal);
        Assert.Contains("ArrowLeft", script, StringComparison.Ordinal);
        Assert.Contains("aria-busy", script, StringComparison.Ordinal);
        Assert.Contains("data-dashboard-detail-link", script, StringComparison.Ordinal);
        Assert.Contains("searchParams.set(\"period\", \"custom\")", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);

        var chart = RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "lib", "chart.js", "chart.umd.min.js");
        var license = RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "lib", "chart.js", "LICENSE.md");
        Assert.True(File.Exists(chart));
        Assert.Contains("The MIT License", File.ReadAllText(license), StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_summary_unifies_navigation_and_reports_require_admin()
    {
        var layout = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml"));
        var summaryNavigation = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "_AdminSummaryNavigation.cshtml"));
        var dashboard = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Dashboard", "Index.cshtml"));
        var executive = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Executive", "Index.cshtml"));
        var workload = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Workload", "Index.cshtml"));

        Assert.Contains(ModuleNavigationTestSupport.Actions(), action => action.Title == "Resumen operativo" && action.Page == "/Reports/Dashboard/Index");
        Assert.Contains(ModuleNavigationTestSupport.Actions(), action => action.Title == "Pendientes" && action.RouteValues["view"] == "pending");
        Assert.DoesNotContain(ModuleNavigationTestSupport.Actions(false), action => action.Title == "Tablero diario");
        Assert.DoesNotContain(ModuleNavigationTestSupport.Actions(false), action => action.Title == "Carga de trabajo");
        Assert.Contains("asp-page=\"/Reports/Dashboard/Index\">@CatTexts[\"Hoy\"]</a>", summaryNavigation, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Reports/Executive/Index\">@CatTexts[\"Gestión\"]</a>", summaryNavigation, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"activity\">@CatTexts[\"Equipo\"]</a>", summaryNavigation, StringComparison.Ordinal);
        Assert.Equal(4, summaryNavigation.Split("aria-current=", StringSplitOptions.None).Length - 1);

        Assert.Contains("_AdminSummaryNavigation.cshtml\", \"today\"", dashboard, StringComparison.Ordinal);
        Assert.Contains("_AdminSummaryNavigation.cshtml\", \"management\"", executive, StringComparison.Ordinal);
        Assert.Contains("Model.IsAdmin && !isPending", workload, StringComparison.Ordinal);
        Assert.Contains("_AdminSummaryNavigation.cshtml\", \"team\"", workload, StringComparison.Ordinal);
        Assert.Contains("@if (!Model.IsAdmin)", workload, StringComparison.Ordinal);
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
