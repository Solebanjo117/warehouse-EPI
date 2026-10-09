using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Reports.Kardex;

namespace WarehouseEPI.Tests.Web;

public sealed class KardexRouteTests
{
    [Fact]
    public async Task Kardex_location_options_preserve_history_and_explicit_selection()
    {
        var (model, db) = CreateModel();
        await using (db)
        {
            var active = new Location { Code = "A-ACTIVE" };
            var historical = new Location { Code = "B-HISTORY", IsActive = false, IsBlocked = true, IsPhysicallyPresent = false };
            var selected = new Location { Code = "C-SELECTED", IsActive = false };
            var excluded = new Location { Code = "D-EMPTY", IsActive = false };
            var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
            var product = new Product { Sku = "OLD-PRODUCT", BaseUnit = unit };
            var otherProduct = new Product { Sku = "OTHER-PRODUCT", BaseUnit = unit };
            var user = new User { FullName = "History", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
            var movement = new InventoryMovement
            {
                OperationId = Guid.NewGuid(), Type = InventoryMovementType.Entry,
                ResponsibleUser = user, OccurredAt = new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero),
                RequestFingerprint = "historical-location"
            };
            var line = new InventoryMovementLine { Movement = movement, Product = product, Unit = unit,
                DestinationLocation = historical, Quantity = 5m, LineNumber = 1 };
            line.BalanceChanges.Add(new InventoryBalanceChange { MovementLine = line, Location = historical,
                DeltaQuantity = 5m, PreviousQuantity = 0m, ResultingQuantity = 5m });
            db.AddRange(active, historical, selected, excluded, otherProduct, line);
            db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = historical, Quantity = 0m });
            await db.SaveChangesAsync();

            await model.OnGetAsync(otherProduct.Sku, null, period: "today");
            Assert.Equal(new[] { active.Id, historical.Id }, model.LocationOptions.Select(x => x.Id));
            Assert.False(model.LocationOptions.Single(x => x.Id == historical.Id).IsActive);
            Assert.Empty(model.Kardex!.Rows);

            await model.OnGetAsync(null, selected.Id, period: "all");
            Assert.Equal(selected.Id, model.LocationId);
            Assert.Equal(new[] { active.Id, historical.Id, selected.Id }, model.LocationOptions.Select(x => x.Id));

            await model.OnGetAsync(product.Sku, historical.Id, period: "all");
            Assert.Equal(historical.Id, model.LocationId);
            Assert.Equal(historical.Id, model.Kardex!.ScopedLocation!.Id);
            Assert.Equal(movement.Id, Assert.Single(model.Kardex.Rows).MovementId);

            var csv = Assert.IsType<FileContentResult>(await model.OnGetExportAsync("csv", product.Sku, historical.Id, period: "all"));
            Assert.Contains(historical.Code, System.Text.Encoding.UTF8.GetString(csv.FileContents));
            var xlsx = Assert.IsType<FileContentResult>(await model.OnGetExportAsync("xlsx", product.Sku, historical.Id, period: "all"));
            using var stream = new MemoryStream(xlsx.FileContents);
            using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
            Assert.Contains($"Ubicación: {historical.Code}", workbook.Worksheet("Kardex").Cell(2, 1).GetString());

            model.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.IsType<ForbidResult>(await model.OnGetExportAsync("xlsx", product.Sku, historical.Id, period: "all"));
            Assert.IsType<ForbidResult>(await model.OnGetExportAsync("csv", product.Sku, historical.Id, period: "all"));
        }
    }

    private static WarehouseDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WarehouseDbContext(options);
    }

    private static (IndexModel Model, WarehouseDbContext Db) CreateModel(bool isAdmin = true)
    {
        var db = CreateDbContext();
        var clock = new WarehouseClock(new WarehouseSettingsService(db));
        var settings = new WarehouseSettingsService(db);
        var kardexService = new KardexReportService(db);
        var exportService = new KardexExportService(settings);

        var model = new IndexModel(kardexService, exportService, clock, settings, db);

        var httpContext = new DefaultHttpContext();
        if (isAdmin)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "AdminUser"),
                new Claim(ClaimTypes.Role, "ADMIN")
            ], "TestAuth"));
        }
        else
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "FloorOperator"),
                new Claim(ClaimTypes.Role, "OPERATOR")
            ], "TestAuth"));
        }

        model.PageContext = new PageContext
        {
            HttpContext = httpContext
        };

        return (model, db);
    }

    [Fact]
    public async Task Kardex_renders_empty_state_when_no_sku_provided()
    {
        var (model, db) = CreateModel();
        await using (db)
        {
            await model.OnGetAsync(sku: null, locationId: null);

            Assert.Null(model.Kardex);
            Assert.Null(model.ErrorMessage);
            Assert.Equal("30", model.Period);
        }
    }

    [Fact]
    public async Task Kardex_period_precedence_supports_quick_all_and_legacy_date_urls()
    {
        var (model, db) = CreateModel();
        await using (db)
        {
            var oldFrom = new DateOnly(2025, 1, 1);
            var oldTo = new DateOnly(2025, 1, 2);
            await model.OnGetAsync(null, null, period: "all", from: oldFrom, to: oldTo);
            Assert.Equal("all", model.Period);
            Assert.Null(model.From);
            Assert.Null(model.To);

            await model.OnGetAsync(null, null, period: "today", from: oldFrom, to: oldTo);
            Assert.Equal("today", model.Period);
            Assert.NotEqual(oldFrom, model.From);

            await model.OnGetAsync(null, null, period: null, from: oldFrom, to: oldTo);
            Assert.Equal("custom", model.Period);
            Assert.Equal(oldFrom, model.From);
            Assert.Equal(oldTo, model.To);
        }
    }

    [Fact]
    public async Task Kardex_handles_non_existent_sku_with_error()
    {
        var (model, db) = CreateModel();
        await using (db)
        {
            await model.OnGetAsync(sku: "NON-EXISTENT", locationId: null);

            Assert.Null(model.Kardex);
            Assert.NotNull(model.ErrorMessage);
            Assert.Contains("NON-EXISTENT", model.ErrorMessage);
        }
    }

    [Fact]
    public async Task Kardex_queries_existing_product_and_populates_results()
    {
        var (model, db) = CreateModel();
        await using (db)
        {
            var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
            var product = new Product { Id = Guid.NewGuid(), Sku = "SKU-WEB-01", Description = "Producto Web", BaseUnitId = 1 };
            var loc = new Location { Id = Guid.NewGuid(), Code = "WEB-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
            var user = new User { Id = Guid.NewGuid(), FullName = "Operador Web", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
            db.AddRange(unit, product, loc, user);

            var now = DateTimeOffset.UtcNow;
            var mov = new InventoryMovement { Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Type = InventoryMovementType.Entry, Purpose = InventoryMovementPurpose.Standard, ResponsibleUser = user, OccurredAt = now, RequestFingerprint = "fp-web" };
            var line = new InventoryMovementLine { Id = Guid.NewGuid(), Movement = mov, Product = product, Unit = unit, DestinationLocation = loc, Quantity = 25m, LineNumber = 1 };
            line.BalanceChanges.Add(new InventoryBalanceChange { Id = Guid.NewGuid(), MovementLine = line, Location = loc, DeltaQuantity = 25m, PreviousQuantity = 0m, ResultingQuantity = 25m });
            db.InventoryMovements.Add(mov);
            db.InventoryMovementLines.Add(line);
            db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = loc.Id, Quantity = 25m });
            await db.SaveChangesAsync();

            await model.OnGetAsync(sku: "SKU-WEB-01", locationId: null, period: "this-month");

            Assert.NotNull(model.Kardex);
            Assert.Equal("SKU-WEB-01", model.Kardex.Product.Sku);
            Assert.Equal(25m, model.Kardex.Summary.CurrentPhysicalBalance);
            Assert.Single(model.Kardex.Rows);
            Assert.Equal(25m, model.Kardex.Rows[0].RunningBalance);
        }
    }

    [Fact]
    public async Task Kardex_export_requires_admin_and_generates_xlsx_and_csv()
    {
        var (operatorModel, db) = CreateModel(isAdmin: false);
        await using (db)
        {
            var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
            var product = new Product { Id = Guid.NewGuid(), Sku = "SKU-EXP-01", Description = "Producto Export", BaseUnitId = 1 };
            var loc = new Location { Id = Guid.NewGuid(), Code = "EXP-01", Kind = LocationKind.Rack, OperationalRole = LocationOperationalRole.Storage };
            var user = new User { Id = Guid.NewGuid(), FullName = "User", PinLookup = "lk", PinHash = "ph", RoleId = 1 };
            db.AddRange(unit, product, loc, user);
            db.InventoryBalances.Add(new InventoryBalance { Id = Guid.NewGuid(), ProductId = product.Id, LocationId = loc.Id, Quantity = 10m });
            await db.SaveChangesAsync();

            // 1. Non-admin operator is forbidden from exporting
            var forbidResult = await operatorModel.OnGetExportAsync(format: "xlsx", sku: "SKU-EXP-01", locationId: null);
            Assert.IsType<ForbidResult>(forbidResult);

            // 2. Admin can export XLSX
            var (adminModel, _) = CreateModel(isAdmin: true);
            adminModel.PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "ADMIN")], "TestAuth"))
                }
            };
            // Share the same DB
            var adminModelWithSameDb = new IndexModel(
                new KardexReportService(db),
                new KardexExportService(new WarehouseSettingsService(db)),
                new WarehouseClock(new WarehouseSettingsService(db)),
                new WarehouseSettingsService(db),
                db);
            adminModelWithSameDb.PageContext = adminModel.PageContext;

            var xlsxResult = await adminModelWithSameDb.OnGetExportAsync(format: "xlsx", sku: "SKU-EXP-01", locationId: null);
            var fileXlsx = Assert.IsType<FileContentResult>(xlsxResult);
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileXlsx.ContentType);
            Assert.Contains("kardex-SKU-EXP-01", fileXlsx.FileDownloadName);
            Assert.True(fileXlsx.FileContents.Length > 0);

            // 3. Admin can export CSV
            var csvResult = await adminModelWithSameDb.OnGetExportAsync(format: "csv", sku: "SKU-EXP-01", locationId: null);
            var fileCsv = Assert.IsType<FileContentResult>(csvResult);
            Assert.Equal("text/csv; charset=utf-8", fileCsv.ContentType);
            Assert.Contains("kardex-SKU-EXP-01", fileCsv.FileDownloadName);
            Assert.True(fileCsv.FileContents.Length > 0);
        }
    }

    [Fact]
    public async Task Kardex_search_includes_inactive_products_and_exact_barcode_opens_canonical_sku()
    {
        var (model, db) = CreateModel();
        await using (db)
        {
            var unit = new Unit { Id = 1, Code = "PZA", Name = "Pieza" };
            var product = new Product
            {
                Sku = "SKU-INACTIVO",
                Description = "Filtro especial",
                ExternalReference = "REF-ABC",
                BaseUnit = unit,
                IsActive = false
            };
            product.Barcodes.Add(new ProductBarcode { Product = product, Barcode = "001234500", IsActive = true });
            db.AddRange(unit, product);
            await db.SaveChangesAsync();

            var response = Assert.IsType<JsonResult>(await model.OnGetProductsAsync("especial"));
            var suggestions = Assert.IsAssignableFrom<IEnumerable<KardexProductSuggestion>>(response.Value);
            var suggestion = Assert.Single(suggestions);
            Assert.Equal("SKU-INACTIVO", suggestion.Sku);
            Assert.False(suggestion.IsActive);

            await model.OnGetAsync("001234500", null, period: "all");
            Assert.NotNull(model.Kardex);
            Assert.Equal("SKU-INACTIVO", model.Sku);
        }
    }

    [Fact]
    public void Kardex_page_preserves_lookup_paging_and_admin_only_correction_details()
    {
        var page = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "Pages", "Reports", "Kardex", "Index.cshtml"));
        var script = File.ReadAllText(RepositoryPath("src", "WarehouseEPI.Web", "wwwroot", "js", "suggestion-lookup.js"));

        Assert.Contains("data-kardex-product-input", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-pageNumber", page, StringComparison.Ordinal);
        Assert.Contains("asp-route-locationId=\"@Model.LocationId\"", page, StringComparison.Ordinal);
        Assert.Contains("selected=\"@(Model.LocationId == opt.Id)\"", page, StringComparison.Ordinal);
        Assert.Contains("CatTexts[\"Inactiva\"]", page, StringComparison.Ordinal);
        foreach (var (resource, expected) in new[] { ("CatalogTexts.resx", "Inactiva"), ("CatalogTexts.en.resx", "Inactive") })
        {
            var document = System.Xml.Linq.XDocument.Load(RepositoryPath("src", "WarehouseEPI.Web", "Resources", "Localization", resource));
            Assert.Equal(expected, document.Root!.Elements("data").Single(x => (string?)x.Attribute("name") == "Inactiva").Element("value")!.Value);
        }
        Assert.Contains("isAdmin && row.Corrections.Count > 0", page, StringComparison.Ordinal);
        Assert.Contains("event.key === \"ArrowDown\"", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Escape\"", script, StringComparison.Ordinal);
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
}
