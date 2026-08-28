using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Web;

public sealed class InventoryAnalyticsRouteTests
{
    [Fact]
    public void Inventory_analytics_is_public_read_only_and_exposes_expected_tabs_and_admin_exports()
    {
        var page = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml"));
        var pageModel = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml.cs"));

        Assert.Contains("@page \"/Reports/Inventory\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorize", pageModel, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"occupancy\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"activity\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"stagnant\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"exceptions\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-exception=\"negative\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-exception=\"minimum\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Export\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-format=\"xlsx\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-format=\"csv\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Inventory/Index\"", page, StringComparison.Ordinal);
        Assert.Contains("OnGetExportAsync", pageModel, StringComparison.Ordinal);
        Assert.Contains("if (!User.IsInRole(\"ADMIN\"))", pageModel, StringComparison.Ordinal);
        Assert.Contains("return Forbid()", pageModel, StringComparison.Ordinal);
        Assert.Contains("10000", pageModel, StringComparison.Ordinal);
        Assert.Contains("GetNegativeAlertExportAsync", pageModel, StringComparison.Ordinal);
        Assert.Contains("GetBelowMinimumAlertExportAsync", pageModel, StringComparison.Ordinal);
        Assert.Contains("posiciones producto-ubicación", pageModel, StringComparison.Ordinal);
        Assert.Contains("productos", pageModel, StringComparison.Ordinal);
        Assert.Contains("report-print-meta", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Administrative_links_are_conditional_and_navigation_connects_dashboard_and_analytics()
    {
        var page = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml"));
        var layout = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Shared", "_Layout.cshtml"));
        var dashboard = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Dashboard", "Index.cshtml"));

        Assert.Contains("User.IsInRole(\"ADMIN\")", page, StringComparison.Ordinal);
        Assert.Contains("if (isAdmin)", page, StringComparison.Ordinal);
        Assert.Contains("/Admin/Catalogs/Products/Details", page, StringComparison.Ordinal);
        Assert.Contains("/Admin/Inventory/Alerts", page, StringComparison.Ordinal);
        Assert.Contains("/Admin/Catalogs/Locations/Index", page, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Reports/Inventory/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("Analítica de inventario", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Reports/Inventory/Index\"", dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_model_normalizes_supported_filters_and_uses_filter_specific_cache()
    {
        var pageModel = File.ReadAllText(RepositoryPath(
            "src", "WarehouseEPI.Web", "Pages", "Reports", "Inventory", "Index.cshtml.cs"));

        Assert.Contains("TimeSpan.FromSeconds(60)", pageModel, StringComparison.Ordinal);
        Assert.Contains("PageSize = 25", pageModel, StringComparison.Ordinal);
        Assert.Contains("\"rotation\" => \"activity\"", pageModel, StringComparison.Ordinal);
        Assert.Contains("\"activity\" or \"stagnant\" or \"exceptions\"", pageModel, StringComparison.Ordinal);
        Assert.Contains("period is \"30\" or \"180\" or \"all\"", pageModel, StringComparison.Ordinal);
        Assert.Contains("status is \"inactive\" or \"all\"", pageModel, StringComparison.Ordinal);
        Assert.Contains("filter.ProductStatus", pageModel, StringComparison.Ordinal);
        Assert.Contains("filter.Search", pageModel, StringComparison.Ordinal);
        Assert.Contains("filter.UnitId", pageModel, StringComparison.Ordinal);
        Assert.Contains("filter.PageNumber", pageModel, StringComparison.Ordinal);
        Assert.Contains("CachedAnalyticsResult", pageModel, StringComparison.Ordinal);
        Assert.Contains("memoryCache.Remove(key)", pageModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inventory_analytics_reports_cached_generation_time_and_manual_refresh_replaces_snapshot()
    {
        await using var db = CreateDbContext();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var model = CreateModel(db, time);

        await model.OnGetAsync("occupancy", null, null, null, null, null, refresh: false);
        var first = model.UpdatedAt;
        time.Advance(TimeSpan.FromMinutes(5));
        await model.OnGetAsync("occupancy", null, null, null, null, null, refresh: false);
        Assert.Equal(first, model.UpdatedAt);

        await model.OnGetAsync("occupancy", null, null, null, null, null, refresh: true);
        Assert.True(model.UpdatedAt > first);
    }

    [Fact]
    public async Task Inventory_analytics_exceptions_are_public_read_only_and_reuse_inventory_alert_population()
    {
        await using var db = CreateDbContext();
        var product = new Product
        {
            Sku = "EXCEPTION-001",
            Description = "Producto para excepción",
            BaseUnitId = 1,
            MinimumStock = 10m
        };
        var location = new Location { Code = "EX-01", Kind = LocationKind.Rack };
        db.AddRange(product, location);
        db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = location, Quantity = -2m });
        await db.SaveChangesAsync();
        var model = CreateModel(db, TimeProvider.System);

        await model.OnGetAsync("exceptions", "negative", null, null, "exception", null);

        Assert.Equal("exceptions", model.View);
        Assert.Equal(1, model.ExceptionSummary.NegativePositions);
        var negative = Assert.Single(model.NegativeExceptions.Items);
        Assert.Equal(product.Id, negative.ProductId);
        Assert.Equal(location.Id, negative.LocationId);

        await model.OnGetAsync("exceptions", "minimum", null, null, "exception", null);
        var minimum = Assert.Single(model.MinimumExceptions.Items);
        Assert.Equal(product.Id, minimum.ProductId);
        Assert.Equal(12m, minimum.Deficit);
    }

    [Fact]
    public async Task Inventory_analytics_export_handler_forbids_anonymous_and_operator_direct_requests()
    {
        await using var db = CreateDbContext();
        var model = CreateModel(db, TimeProvider.System);

        Assert.IsType<ForbidResult>(
            await model.OnGetExportAsync("activity", null, "csv", null, null, null, null));

        model.HttpContext.User = Principal("OPERATOR");
        Assert.IsType<ForbidResult>(
            await model.OnGetExportAsync("activity", null, "csv", null, null, null, null));
    }

    [Fact]
    public async Task Inventory_analytics_export_handler_allows_admin_direct_requests()
    {
        await using var db = CreateDbContext();
        var model = CreateModel(db, TimeProvider.System);
        model.HttpContext.User = Principal("ADMIN");

        var result = await model.OnGetExportAsync("activity", null, "csv", null, null, null, null);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.EndsWith(".csv", file.FileDownloadName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inventory_analytics_exception_export_requires_admin_and_accepts_the_selected_population()
    {
        await using var db = CreateDbContext();
        var product = new Product { Sku = "EX-CSV", BaseUnitId = 1, MinimumStock = 3m };
        var location = new Location { Code = "EX-CSV-01", Kind = LocationKind.Rack };
        db.AddRange(product, location);
        db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = location, Quantity = -1m });
        await db.SaveChangesAsync();
        var model = CreateModel(db, TimeProvider.System);

        Assert.IsType<ForbidResult>(await model.OnGetExportAsync("exceptions", "negative", "csv", null, null, null, null));
        model.HttpContext.User = Principal("ADMIN");
        var result = Assert.IsType<FileContentResult>(await model.OnGetExportAsync("exceptions", "negative", "csv", null, null, null, null));
        Assert.Equal("text/csv; charset=utf-8", result.ContentType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, result.FileContents[..3]);
    }

    private static WarehouseEPI.Web.Pages.Reports.Inventory.IndexModel CreateModel(
        WarehouseDbContext db,
        TimeProvider timeProvider)
    {
        var settings = new WarehouseSettingsService(db);
        return new(
            new InventoryAnalyticsService(db, settings),
            new InventoryQueryService(db),
            new ReportExportService(settings),
            db,
            new WarehouseClock(settings),
            settings,
            new MemoryCache(Options.Create(new MemoryCacheOptions())),
            timeProvider)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"InventoryAnalyticsRouteTests-{Guid.NewGuid():N}")
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

    private static ClaimsPrincipal Principal(string role) => new(
        new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

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
