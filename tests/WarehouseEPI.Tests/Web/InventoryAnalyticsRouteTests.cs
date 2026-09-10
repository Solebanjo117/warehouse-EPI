using System.Security.Claims;
using System.Text;
using ClosedXML.Excel;
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
        Assert.Contains("asp-route-view=\"lot-aging\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"coverage\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-view=\"exceptions\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-exception=\"negative\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-exception=\"minimum\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Export\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-format=\"xlsx\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-format=\"csv\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-stagnantCategory=\"@stagnantCategory\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-ageBucket=\"@ageBucket\"", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-coverageClass=\"@coverageClass\"", page, StringComparison.Ordinal);
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
        Assert.Contains("period is \"30\" or \"180\" or \"all\" or \"this-month\" or \"last-month\"", pageModel, StringComparison.Ordinal);
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

    [Fact]
    public async Task Inventory_analytics_occupancy_export_requires_admin_and_supports_xlsx_and_csv()
    {
        await using var db = CreateDbContext();
        var location = new Location { Code = "A-01-01", Kind = LocationKind.Rack, IsActive = true };
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var model = CreateModel(db, TimeProvider.System);

        // Anonymous -> Forbid
        Assert.IsType<ForbidResult>(await model.OnGetExportAsync("occupancy", null, "xlsx", null, null, null, null));
        Assert.IsType<ForbidResult>(await model.OnGetExportAsync("occupancy", null, "csv", null, null, null, null));

        // Operator -> Forbid
        model.HttpContext.User = Principal("OPERATOR");
        Assert.IsType<ForbidResult>(await model.OnGetExportAsync("occupancy", null, "xlsx", null, null, null, null));

        // Admin -> FileContentResult (.xlsx and .csv)
        model.HttpContext.User = Principal("ADMIN");
        var xlsxResult = Assert.IsType<FileContentResult>(await model.OnGetExportAsync("occupancy", null, "xlsx", null, null, null, null));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsxResult.ContentType);
        Assert.StartsWith("ocupacion-almacen-", xlsxResult.FileDownloadName, StringComparison.Ordinal);
        Assert.EndsWith(".xlsx", xlsxResult.FileDownloadName, StringComparison.Ordinal);

        var csvResult = Assert.IsType<FileContentResult>(await model.OnGetExportAsync("occupancy", null, "csv", null, null, null, null));
        Assert.Equal("text/csv; charset=utf-8", csvResult.ContentType);
        Assert.StartsWith("ocupacion-almacen-", csvResult.FileDownloadName, StringComparison.Ordinal);
        Assert.EndsWith(".csv", csvResult.FileDownloadName, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csvResult.FileContents[..3]);
    }

    [Fact]
    public async Task Inventory_analytics_stagnant_filters_real_data_and_restricts_to_90plus()
    {
        await using var db = CreateDbContext();
        var nowUtc = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(nowUtc);

        var loc = new Location { Code = "A-01-02", Kind = LocationKind.Rack, IsActive = true };
        var user = new User { FullName = "Admin", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
        var pRecent = new Product { Sku = "STG-RECENT", Description = "Reciente", BaseUnitId = 1, IsActive = true };
        var p45 = new Product { Sku = "STG-45D", Description = "45 días", BaseUnitId = 1, IsActive = true };
        var p75 = new Product { Sku = "STG-75D", Description = "75 días", BaseUnitId = 1, IsActive = true };
        var p100 = new Product { Sku = "STG-100D", Description = "100 días", BaseUnitId = 1, IsActive = true };
        var pNever = new Product { Sku = "STG-NEVER", Description = "Nunca salió", BaseUnitId = 1, IsActive = true };
        var pZero = new Product { Sku = "STG-ZERO", Description = "Sin saldo", BaseUnitId = 1, IsActive = true };

        db.AddRange(loc, user, pRecent, p45, p75, p100, pNever, pZero);

        // Saldos
        db.InventoryBalances.AddRange(
            new InventoryBalance { Product = pRecent, Location = loc, Quantity = 10m },
            new InventoryBalance { Product = p45, Location = loc, Quantity = 10m },
            new InventoryBalance { Product = p75, Location = loc, Quantity = 10m },
            new InventoryBalance { Product = p100, Location = loc, Quantity = 10m },
            new InventoryBalance { Product = pNever, Location = loc, Quantity = 10m },
            new InventoryBalance { Product = pZero, Location = loc, Quantity = 0m }
        );

        // Salidas efectivas
        void AddExit(Product p, DateTimeOffset occurredAt, string refCode)
        {
            var mov = new InventoryMovement
            {
                Type = InventoryMovementType.Exit,
                Purpose = InventoryMovementPurpose.Standard,
                ResponsibleUser = user,
                OccurredAt = occurredAt,
                Reference = refCode,
                RequestFingerprint = Guid.NewGuid().ToString()
            };
            mov.Lines.Add(new InventoryMovementLine
            {
                Product = p,
                UnitId = 1,
                SourceLocation = loc,
                Quantity = 1m,
                LineNumber = 1
            });
            db.InventoryMovements.Add(mov);
        }

        AddExit(pRecent, nowUtc.AddDays(-5), "REF-REC");
        AddExit(p45, nowUtc.AddDays(-45), "REF-45");
        AddExit(p75, nowUtc.AddDays(-75), "REF-75");
        AddExit(p100, nowUtc.AddDays(-100), "REF-100");
        AddExit(pZero, nowUtc.AddDays(-120), "REF-ZERO");

        await db.SaveChangesAsync();

        var model = CreateModel(db, time);
        model.HttpContext.User = Principal("ADMIN");

        // 1. Sin filtro de categoría (muestra todos los estancados con stock > 0: Never, 100D, 75D, 45D)
        await model.OnGetAsync("stagnant", null, null, null, null, null, stagnantCategory: null);
        Assert.Equal(4, model.Stagnant.TotalCount);
        var skusUnfiltered = model.Stagnant.Items.Select(x => x.Sku).ToHashSet();
        Assert.Contains("STG-NEVER", skusUnfiltered);
        Assert.Contains("STG-100D", skusUnfiltered);
        Assert.Contains("STG-75D", skusUnfiltered);
        Assert.Contains("STG-45D", skusUnfiltered);
        Assert.DoesNotContain("STG-RECENT", skusUnfiltered);
        Assert.DoesNotContain("STG-ZERO", skusUnfiltered);

        // 2. Con filtro 90plus (solo Never y 100D)
        await model.OnGetAsync("stagnant", null, null, null, null, null, stagnantCategory: "90plus");
        Assert.Equal(2, model.Stagnant.TotalCount);
        var skus90Plus = model.Stagnant.Items.Select(x => x.Sku).ToHashSet();
        Assert.Contains("STG-NEVER", skus90Plus);
        Assert.Contains("STG-100D", skus90Plus);
        Assert.DoesNotContain("STG-75D", skus90Plus);
        Assert.DoesNotContain("STG-45D", skus90Plus);
        Assert.Equal("90plus", model.StagnantCategory);
        Assert.Equal(StagnantCategory.Days90Plus, model.StagnantCategoryFilter);

        // 3. Exportación CSV con 90plus
        var csvResult = Assert.IsType<FileContentResult>(
            await model.OnGetExportAsync("stagnant", null, "csv", null, null, null, null, stagnantCategory: "90plus"));
        var csvContent = System.Text.Encoding.UTF8.GetString(csvResult.FileContents[3..]);
        Assert.Contains("STG-NEVER", csvContent);
        Assert.Contains("STG-100D", csvContent);
        Assert.DoesNotContain("STG-75D", csvContent);
        Assert.DoesNotContain("STG-45D", csvContent);
        Assert.DoesNotContain("STG-RECENT", csvContent);
        Assert.DoesNotContain("STG-ZERO", csvContent);
        Assert.Contains("categoría=90+ días", csvContent);

        // 4. Exportación Excel con 90plus
        var xlsxResult = Assert.IsType<FileContentResult>(
            await model.OnGetExportAsync("stagnant", null, "xlsx", null, null, null, null, stagnantCategory: "90plus"));
        using (var wb = new XLWorkbook(new MemoryStream(xlsxResult.FileContents)))
        {
            var sheet = wb.Worksheet("Estancamiento");
            Assert.Contains("categoría=90+ días", sheet.Cell(3, 1).GetString());
            var dataSkus = new[] { sheet.Row(6).Cell(1).GetString(), sheet.Row(7).Cell(1).GetString() };
            Assert.Contains("STG-NEVER", dataSkus);
            Assert.Contains("STG-100D", dataSkus);
            Assert.True(sheet.Row(8).Cell(1).IsEmpty()); // Exactamente 2 filas de datos
        }

        // 5. Categoría no soportada / inválida (e.g. "never") se normaliza a null
        await model.OnGetAsync("stagnant", null, null, null, null, null, stagnantCategory: "never");
        Assert.Null(model.StagnantCategory);
        Assert.Null(model.StagnantCategoryFilter);
        Assert.Equal(4, model.Stagnant.TotalCount);
    }

    [Fact]
    public async Task Inventory_analytics_normalizes_this_month_and_last_month_periods()
    {
        await using var db = CreateDbContext();
        var model = CreateModel(db, TimeProvider.System);

        await model.OnGetAsync("activity", null, "this-month", null, null, null);
        Assert.Equal("this-month", model.Period);

        await model.OnGetAsync("activity", null, "last-month", null, null, null);
        Assert.Equal("last-month", model.Period);

        await model.OnGetAsync("stagnant", null, null, null, null, null, stagnantCategory: "90plus");
        Assert.Equal("90plus", model.StagnantCategory);
        Assert.Equal(StagnantCategory.Days90Plus, model.StagnantCategoryFilter);

        await model.OnGetAsync("stagnant", null, null, null, null, null, stagnantCategory: "never");
        Assert.Null(model.StagnantCategory);
        Assert.Null(model.StagnantCategoryFilter);
    }

    [Fact]
    public async Task Lot_aging_and_coverage_views_and_exports_work_for_admin_and_forbid_operator()
    {
        await using var db = CreateDbContext();
        var product = new Product { Sku = "SKU-ROUTE-3", BaseUnitId = 1, IsActive = true };
        var storage = new Location { Code = "RACK-R3", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage, RowCode = "R3", IsActive = true };
        var lot = new ProductLot { Product = product, ProductId = product.Id, Number = "AUTO-20260801", NormalizedNumber = "AUTO-20260801", LotDate = new DateOnly(2026, 8, 1) };
        db.AddRange(product, storage, lot);
        db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = storage, Lot = lot, LotId = lot.Id, Quantity = 50m });
        await db.SaveChangesAsync();

        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var model = CreateModel(db, time);

        // 1. Non-admin is forbidden from exporting lot-aging and coverage
        model.PageContext.HttpContext.User = Principal("OPERATOR");
        var forbidAging = await model.OnGetExportAsync("lot-aging", null, "xlsx", null, null, null, null);
        Assert.IsType<ForbidResult>(forbidAging);

        var forbidCoverage = await model.OnGetExportAsync("coverage", null, "xlsx", null, null, null, null);
        Assert.IsType<ForbidResult>(forbidCoverage);

        // 2. Admin can export lot-aging to Excel and CSV
        model.PageContext.HttpContext.User = Principal("ADMIN");
        var agingXlsx = Assert.IsType<FileContentResult>(
            await model.OnGetExportAsync("lot-aging", null, "xlsx", null, null, null, null));
        Assert.StartsWith("antiguedad-lotes-", agingXlsx.FileDownloadName, StringComparison.Ordinal);
        Assert.NotEmpty(agingXlsx.FileContents);

        var agingCsv = Assert.IsType<FileContentResult>(
            await model.OnGetExportAsync("lot-aging", null, "csv", null, null, null, null));
        Assert.StartsWith("antiguedad-lotes-", agingCsv.FileDownloadName, StringComparison.Ordinal);
        var agingCsvText = Encoding.UTF8.GetString(agingCsv.FileContents);
        Assert.Contains("SKU-ROUTE-3", agingCsvText);
        Assert.Contains("AUTO-20260801", agingCsvText);

        // 3. Admin can export coverage to Excel and CSV
        var coverageXlsx = Assert.IsType<FileContentResult>(
            await model.OnGetExportAsync("coverage", null, "xlsx", "30", null, null, null));
        Assert.StartsWith("cobertura-consumo-", coverageXlsx.FileDownloadName, StringComparison.Ordinal);

        var coverageCsv = Assert.IsType<FileContentResult>(
            await model.OnGetExportAsync("coverage", null, "csv", "30", null, null, null));
        Assert.StartsWith("cobertura-consumo-", coverageCsv.FileDownloadName, StringComparison.Ordinal);
        var coverageCsvText = Encoding.UTF8.GetString(coverageCsv.FileContents);
        Assert.Contains("SKU-ROUTE-3", coverageCsvText);
        Assert.Contains("Sin consumo reciente", coverageCsvText);

        // 4. View pages load data
        await model.OnGetAsync("lot-aging", null, null, null, null, null);
        Assert.Equal("lot-aging", model.View);
        Assert.Single(model.LotAging.Page.Items);
        Assert.Equal("SKU-ROUTE-3", model.LotAging.Page.Items[0].Sku);

        await model.OnGetAsync("coverage", null, null, null, null, null);
        Assert.Equal("coverage", model.View);
        Assert.Single(model.Coverage.Page.Items);
        Assert.Equal("SKU-ROUTE-3", model.Coverage.Page.Items[0].Sku);
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
