using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

namespace WarehouseEPI.Tests.Catalogs;

public sealed class ProductCatalogTests
{
    [Fact]
    public async Task Seeded_catalogs_match_the_approved_excel_values()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();

        Assert.Equal(18, await db.Units.CountAsync());
        Assert.Equal(2, await db.ProductTypes.CountAsync());
        Assert.Equal(26, await db.ProductClasses.CountAsync());
        Assert.All(await db.Units.ToListAsync(), unit => Assert.True(unit.AllowsDecimals));
        Assert.Contains(await db.ProductTypes.ToListAsync(), item => item.Code == "FG");
        Assert.Contains(await db.ProductClasses.ToListAsync(), item => item.Code == "2-BBAGS");
        Assert.Contains(await db.Units.ToListAsync(), item =>
            item.Code == "UNASSIGNED" && item.Name == "Sin asignar" && item.IsActive);
    }

    [Fact]
    public async Task Product_validation_normalizes_and_rejects_duplicate_sku_and_expiration_without_lots()
    {
        await using var db = CreateContext(); await db.Database.EnsureCreatedAsync();
        db.Products.Add(new Product { Sku = "SKU-001", BaseUnitId = 1 }); await db.SaveChangesAsync();
        var input = new ProductInputModel { Sku = " sku-001 ", Description = " Descripción ", BaseUnitId = 1, TracksExpiration = true };
        ProductPageSupport.Normalize(input); var state = new ModelStateDictionary();

        await ProductPageSupport.ValidateAsync(db, input, state, CancellationToken.None);

        Assert.Equal("SKU-001", input.Sku);
        Assert.Equal("Descripción", input.Description);
        Assert.True(state.ContainsKey("Input.Sku"));
        Assert.True(state.ContainsKey("Input.TracksExpiration"));
    }

    [Fact]
    public async Task External_reference_can_repeat()
    {
        await using var db = CreateContext(); await db.Database.EnsureCreatedAsync();
        db.Products.AddRange(
            new Product { Sku = "SKU-001", BaseUnitId = 1, ExternalReference = "SOURCE:A" },
            new Product { Sku = "SKU-002", BaseUnitId = 1, ExternalReference = "SOURCE:A" });

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Products.CountAsync(x => x.ExternalReference == "SOURCE:A"));
    }

    [Fact]
    public void Description_is_optional_trimmed_and_has_no_180_character_limit()
    {
        var empty = new ProductInputModel { Sku = "SKU-EMPTY", Description = "   " };
        ProductPageSupport.Normalize(empty);
        Assert.Null(empty.Description);

        var longDescription = new string('D', 300);
        var input = new ProductInputModel { Sku = "SKU-LONG", Description = $"  {longDescription}  " };
        ProductPageSupport.Normalize(input);
        var product = new Product { Sku = input.Sku };
        ProductPageSupport.Apply(product, input);

        Assert.Equal(longDescription, product.Description);
    }

    [Fact]
    public void Product_search_uses_description_reference_barcode_and_active_location_without_name()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .Options;
        using var db = new WarehouseDbContext(options);

        var sql = ProductPageSupport.ApplySearch(db.Products, "needle").ToQueryString();

        Assert.Contains("description", sql);
        Assert.Contains("external_reference", sql);
        Assert.Contains("product_barcodes", sql);
        Assert.Contains("product_location_assignments", sql);
        Assert.Contains("locations", sql);
        Assert.Contains("is_active", sql);
        Assert.DoesNotContain("products\".\"name", sql);
    }

    [Fact]
    public async Task Product_search_by_rack_returns_only_products_with_an_active_assignment()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var assigned = new Product { Sku = "ASSIGNED", BaseUnitId = 1 };
        var inactive = new Product { Sku = "INACTIVE", BaseUnitId = 1 };
        var location = new Location
        {
            Code = "A-1-8",
            Kind = LocationKind.Rack,
            RowCode = "A",
            RackNumber = 1,
            PalletNumber = 8
        };
        db.AddRange(assigned, inactive, location);
        db.ProductLocationAssignments.AddRange(
            new ProductLocationAssignment { Product = assigned, Location = location },
            new ProductLocationAssignment { Product = inactive, Location = location, IsActive = false });
        await db.SaveChangesAsync();

        var matches = await ProductPageSupport.ApplySearch(db.Products, " a-1-8 ").ToListAsync();

        Assert.Collection(matches, product => Assert.Equal("ASSIGNED", product.Sku));
    }

    [Fact]
    public async Task Setting_a_new_primary_barcode_clears_the_previous_one()
    {
        await using var db = CreateContext(); await db.Database.EnsureCreatedAsync();
        var product = new Product { Sku = "SKU-001", BaseUnitId = 1 }; db.Products.Add(product); await db.SaveChangesAsync();
        var page = CreateEditModel(db);
        page.BarcodeInput = new EditModel.BarcodeInputModel { Barcode = "CODE-1", IsPrimary = true };
        await page.OnPostAddBarcodeAsync(product.Id, page.BarcodeInput, CancellationToken.None);
        page.BarcodeInput = new EditModel.BarcodeInputModel { Barcode = "CODE-2", IsPrimary = true };
        await page.OnPostAddBarcodeAsync(product.Id, page.BarcodeInput, CancellationToken.None);

        Assert.Equal(2, await db.ProductBarcodes.CountAsync());
        Assert.Equal("CODE-2", (await db.ProductBarcodes.SingleAsync(x => x.IsPrimary)).Barcode);
    }

    [Fact]
    public async Task Barcode_reserved_by_another_product_is_rejected()
    {
        await using var db = CreateContext(); await db.Database.EnsureCreatedAsync();
        var first = new Product { Sku = "SKU-001", BaseUnitId = 1 };
        var second = new Product { Sku = "SKU-002", BaseUnitId = 1 };
        db.Products.AddRange(first, second); db.ProductBarcodes.Add(new ProductBarcode { Product = first, Barcode = "RESERVED" }); await db.SaveChangesAsync();
        var page = CreateEditModel(db); page.BarcodeInput = new EditModel.BarcodeInputModel { Barcode = " RESERVED " };

        await page.OnPostAddBarcodeAsync(second.Id, page.BarcodeInput, CancellationToken.None);

        Assert.Contains("otro producto", page.TempData["Error"]?.ToString());
        Assert.Single(await db.ProductBarcodes.ToListAsync());
    }

    [Fact]
    public async Task Unassigned_unit_cannot_be_edited_or_deactivated()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var unit = await db.Units.SingleAsync(candidate => candidate.Code == "UNASSIGNED");
        var page = new WarehouseEPI.Web.Pages.Admin.Catalogs.Units.IndexModel(db)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new MemoryTempDataProvider()),
            Input = new WarehouseEPI.Web.Pages.Admin.Catalogs.Units.IndexModel.InputModel
            {
                Id = unit.Id,
                Code = "CHANGED",
                Name = "Cambiada",
                AllowsDecimals = false
            }
        };

        await page.OnPostSaveAsync(CancellationToken.None);
        await page.OnPostToggleAsync(unit.Id, CancellationToken.None);

        db.ChangeTracker.Clear();
        var unchanged = await db.Units.SingleAsync(candidate => candidate.Id == unit.Id);
        Assert.Equal("UNASSIGNED", unchanged.Code);
        Assert.Equal("Sin asignar", unchanged.Name);
        Assert.True(unchanged.IsActive);
        Assert.True(unchanged.AllowsDecimals);
    }

    private static WarehouseDbContext CreateContext() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EditModel CreateEditModel(WarehouseDbContext db)
    {
        var page = new EditModel(db, new ProductLocationAssignmentService(db));
        page.TempData = new TempDataDictionary(new DefaultHttpContext(), new MemoryTempDataProvider());
        return page;
    }

    private sealed class MemoryTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> data = [];
        public IDictionary<string, object> LoadTempData(HttpContext context) => data;
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) => data = new(values);
    }
}
