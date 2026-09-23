using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Locations;
using ExecutiveModel = WarehouseEPI.Web.Pages.Reports.Executive.IndexModel;
using HeatmapModel = WarehouseEPI.Web.Pages.Reports.Heatmap.IndexModel;
using WorkloadModel = WarehouseEPI.Web.Pages.Reports.Workload.IndexModel;

namespace WarehouseEPI.Tests.Web;

public sealed class Etapa4RouteTests
{
    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new WarehouseDbContext(options);
        db.BusinessSettings.Add(new BusinessSettings
        {
            BusinessName = "EPI Global",
            WarehouseName = "Almacén Central",
            WarehouseCode = "WH-01",
            TimeZoneId = "America/Mexico_City"
        });
        db.SaveChanges();
        return db;
    }

    private static PageContext CreatePageContext(bool isAdmin)
    {
        var httpContext = new DefaultHttpContext();
        var role = isAdmin ? "ADMIN" : "OPERATOR";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, isAdmin ? "AdminUser" : "OperatorUser"),
            new Claim(ClaimTypes.Role, role)
        ], "TestAuth"));

        return new PageContext { HttpContext = httpContext };
    }

    [Fact]
    public async Task Workload_page_loads_and_rbac_protects_exports()
    {
        await using var db = CreateDbContext();
        var settingsService = new WarehouseSettingsService(db);
        var clock = new WarehouseClock(settingsService);
        var queueService = new WorkQueueService(db, settingsService);
        var workloadService = new WorkloadReportService(db, settingsService);
        var exportService = new ReportExportService(settingsService);

        var adminModel = new WorkloadModel(queueService, workloadService, exportService, clock, db)
        {
            PageContext = CreatePageContext(isAdmin: true)
        };

        // 1. GET page loads
        await adminModel.OnGetAsync(CancellationToken.None);
        Assert.NotNull(adminModel.Queue);
        Assert.Equal("pending", adminModel.View);

        adminModel.View = "activity";
        await adminModel.OnGetAsync(CancellationToken.None);
        Assert.True(adminModel.Report!.IncludesOperatorDetails);

        // 2. Export as ADMIN succeeds (Excel & CSV)
        var excelResult = await adminModel.OnGetExportAsync("excel", CancellationToken.None);
        var fileExcel = Assert.IsType<FileContentResult>(excelResult);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileExcel.ContentType);

        var csvResult = await adminModel.OnGetExportAsync("csv", CancellationToken.None);
        var fileCsv = Assert.IsType<FileContentResult>(csvResult);
        Assert.Equal("text/csv; charset=utf-8", fileCsv.ContentType);

        // 3. Export as OPERATOR is forbidden
        var operatorModel = new WorkloadModel(queueService, workloadService, exportService, clock, db)
        {
            PageContext = CreatePageContext(isAdmin: false),
            View = "activity",
            UserId = Guid.NewGuid(),
            Search = "nombre privado"
        };
        await operatorModel.OnGetAsync(CancellationToken.None);
        Assert.False(operatorModel.Report!.IncludesOperatorDetails);
        Assert.Empty(operatorModel.Report.Operators);
        Assert.Null(operatorModel.UserId);
        Assert.Null(operatorModel.Search);
        var forbiddenResult = await operatorModel.OnGetExportAsync("excel", CancellationToken.None);
        Assert.IsType<ForbidResult>(forbiddenResult);
    }

    [Fact]
    public async Task Heatmap_page_loads_and_rbac_protects_exports()
    {
        await using var db = CreateDbContext();
        var settingsService = new WarehouseSettingsService(db);
        var clock = new WarehouseClock(settingsService);
        var mapService = new WarehouseMapService(db);
        var heatmapService = new HeatmapReportService(db, mapService, settingsService);
        var exportService = new ReportExportService(settingsService);

        var adminModel = new HeatmapModel(heatmapService, exportService, clock)
        {
            PageContext = CreatePageContext(isAdmin: true),
            Metric = "access-frequency",
            Period = "custom",
            From = new DateOnly(2026, 8, 10),
            To = new DateOnly(2026, 8, 1),
            RowCode = " A ",
            Search = " rack "
        };

        // 1. Legacy GET redirects ADMIN to the administrative Locations mode
        var redirect = Assert.IsType<RedirectToPageResult>(adminModel.OnGet());
        Assert.Equal("/Admin/Catalogs/Locations/Index", redirect.PageName);
        Assert.Equal("activity", redirect.RouteValues!["mapMetric"]);
        Assert.Equal("custom", redirect.RouteValues["period"]);
        Assert.Equal(new DateOnly(2026, 8, 10), redirect.RouteValues["from"]);
        Assert.Equal(new DateOnly(2026, 8, 1), redirect.RouteValues["to"]);
        Assert.Equal(" A ", redirect.RouteValues["rowCode"]);
        Assert.Equal(" rack ", redirect.RouteValues["search"]);

        // 2. Export as ADMIN succeeds
        var excelResult = await adminModel.OnGetExportAsync("excel", CancellationToken.None);
        var fileExcel = Assert.IsType<FileContentResult>(excelResult);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileExcel.ContentType);

        // 3. Export as OPERATOR is forbidden
        var operatorModel = new HeatmapModel(heatmapService, exportService, clock)
        {
            PageContext = CreatePageContext(isAdmin: false),
            Metric = "occupancy-density"
        };
        var publicRedirect = Assert.IsType<RedirectToPageResult>(operatorModel.OnGet());
        Assert.Equal("/Locations/Index", publicRedirect.PageName);
        Assert.Equal("occupancy", publicRedirect.RouteValues!["mapMetric"]);
        var forbiddenResult = await operatorModel.OnGetExportAsync("excel", CancellationToken.None);
        Assert.IsType<ForbidResult>(forbiddenResult);
    }

    [Fact]
    public async Task Heatmap_query_normalizer_preserves_canonical_export_filters()
    {
        await using var db = CreateDbContext();
        var clock = new WarehouseClock(new WarehouseSettingsService(db));
        var from = new DateOnly(2026, 8, 10);
        var to = new DateOnly(2026, 8, 1);

        Assert.Equal("activity", HeatmapQueryNormalizer.NormalizeLegacyMetric("access-frequency", "occupancy"));
        Assert.Equal("occupancy", HeatmapQueryNormalizer.NormalizeLegacyMetric("occupancy-density", "activity"));

        var query = await HeatmapQueryNormalizer.BuildAsync(
            "activity", "custom", from, to, clock, " A ", " rack ", CancellationToken.None);

        Assert.Equal("activity", query.MapMetric);
        Assert.Equal("custom", query.Period);
        Assert.Equal(to, query.From);
        Assert.Equal(from, query.To);
        Assert.Equal(HeatmapMetricType.AccessFrequency, query.Filter.Metric);
        Assert.Equal("A", query.Filter.RowCode);
        Assert.Equal("rack", query.Filter.Search);
        Assert.Equal("01/08/2026 a 10/08/2026", query.PeriodLabel);
    }

    [Fact]
    public async Task Executive_page_loads_and_rbac_protects_exports()
    {
        await using var db = CreateDbContext();
        var settingsService = new WarehouseSettingsService(db);
        var clock = new WarehouseClock(settingsService);
        var analyticsService = new InventoryAnalyticsService(db, settingsService);
        var executiveService = new ExecutiveReportService(db, settingsService, analyticsService);
        var exportService = new ReportExportService(settingsService);

        var adminModel = new ExecutiveModel(executiveService, exportService, clock, TimeProvider.System)
        {
            PageContext = CreatePageContext(isAdmin: true)
        };

        // 1. GET page loads
        await adminModel.OnGetAsync(CancellationToken.None);
        Assert.NotNull(adminModel.Report);
        Assert.Equal("this-month", adminModel.Period);

        // 2. Export as ADMIN succeeds
        var excelResult = await adminModel.OnGetExportAsync(CancellationToken.None);
        var fileExcel = Assert.IsType<FileContentResult>(excelResult);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileExcel.ContentType);

        // 3. Export as OPERATOR is forbidden
        var operatorModel = new ExecutiveModel(executiveService, exportService, clock, TimeProvider.System)
        {
            PageContext = CreatePageContext(isAdmin: false)
        };
        var forbiddenResult = await operatorModel.OnGetExportAsync(CancellationToken.None);
        Assert.IsType<ForbidResult>(forbiddenResult);
    }
}
