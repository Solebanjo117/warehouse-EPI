using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Core;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Imports;

namespace WarehouseEPI.Tests.Imports;

public sealed class ProductImportServiceTests
{
    [Fact]
    public async Task Preview_does_not_write_and_confirmation_uses_import_defaults_once()
    {
        await using var fixture = Fixture.Create();
        using var stream = Workbook(("RM", " sku-new ", " Descripción larga ", "Each (EA)", " REF "));

        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner);

        Assert.Equal(0, await fixture.Db.Products.CountAsync());
        Assert.True(preview.CanConfirm);
        Assert.Equal(1, preview.NewCount);

        var result = await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner);
        var saved = await fixture.Db.Products.SingleAsync();
        Assert.True(result.Succeeded);
        Assert.Equal("SKU-NEW", saved.Sku);
        Assert.Equal("Descripción larga", saved.Description);
        Assert.Equal("REF", saved.ExternalReference);
        Assert.Null(saved.ProductTypeId);
        Assert.NotNull(saved.ProductClassId);
        Assert.Equal(0m, saved.MinimumStock);
        Assert.False(saved.TracksLots);
        Assert.False(saved.TracksExpiration);
        Assert.True(saved.AllowsNegativeStock);
        Assert.True(saved.IsActive);

        var reused = await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner);
        Assert.False(reused.Succeeded);
        Assert.Equal(1, await fixture.Db.Products.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_active_or_inactive_sku_is_skipped_without_changes(bool isActive)
    {
        await using var fixture = Fixture.Create();
        fixture.Db.Products.Add(new Product { Sku = "EXISTING", Description = "Manual", BaseUnitId = 1, IsActive = isActive });
        await fixture.Db.SaveChangesAsync();
        using var stream = Workbook(("RM", "EXISTING", "Desde Excel", "Each (EA)", "NEW-REF"));

        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner);
        var result = await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner);

        Assert.Equal(1, preview.ExistingCount);
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Inserted);
        var saved = await fixture.Db.Products.SingleAsync();
        Assert.Equal("Manual", saved.Description);
        Assert.Null(saved.ExternalReference);
        Assert.Equal(isActive, saved.IsActive);
    }

    [Fact]
    public async Task Unknown_or_inactive_catalogs_block_confirmation()
    {
        await using var fixture = Fixture.Create();
        var unit = await fixture.Db.Units.SingleAsync(candidate => candidate.Code == "EA");
        unit.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        using var stream = Workbook(("UNKNOWN", "SKU", "D", "Each (EA)", ""));

        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner);
        var result = await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner);

        Assert.True(preview.ErrorCount >= 1);
        Assert.False(preview.CanConfirm);
        Assert.False(result.Succeeded);
        Assert.Empty(fixture.Db.Products);
    }

    [Fact]
    public async Task Token_is_bound_to_owner_and_expires_after_thirty_minutes()
    {
        await using var fixture = Fixture.Create();
        using var stream = Workbook(("RM", "SKU", "D", "Each (EA)", ""));
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner);

        Assert.False(fixture.Service.TryGetPreview(preview.Token, Guid.NewGuid(), out _));
        fixture.Clock.Advance(ProductImportPreviewStore.Lifetime + TimeSpan.FromSeconds(1));
        Assert.False(fixture.Service.TryGetPreview(preview.Token, fixture.Owner, out _));
        Assert.False((await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner)).Succeeded);
    }

    [Fact]
    public async Task Blank_source_unit_is_confirmed_as_unassigned()
    {
        await using var fixture = Fixture.Create();
        using var stream = Workbook(("RM", "SKU-WITHOUT-UNIT", "D", "", ""));

        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner);
        var result = await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner);

        Assert.True(preview.CanConfirm);
        Assert.Equal(1, preview.WarningCount);
        Assert.True(result.Succeeded);
        var product = await fixture.Db.Products.Include(candidate => candidate.BaseUnit).SingleAsync();
        Assert.Equal("UNASSIGNED", product.BaseUnit.Code);
        Assert.Equal("Sin asignar", product.BaseUnit.Name);
    }

    [Fact]
    public async Task Configured_real_workbook_previews_all_unique_skus_without_errors()
    {
        var path = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_PRODUCT_WORKBOOK");
        if (string.IsNullOrWhiteSpace(path))
            return;
        await using var fixture = Fixture.Create();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var preview = await fixture.Service.PrepareAsync(stream, Path.GetFileName(path), fixture.Owner);

        Assert.True(preview.CanConfirm);
        Assert.Equal(1_613, preview.SourceRowCount);
        Assert.Equal(1_612, preview.NewCount);
        Assert.Equal(0, preview.ExistingCount);
        Assert.Equal(0, preview.ErrorCount);
        Assert.Equal(1, preview.ConsolidatedCount);
        Assert.Equal(65, preview.Rows.Count(row => row.UnitCode == CatalogDefaults.UnassignedUnitCode));
        Assert.Equal(0, await fixture.Db.Products.CountAsync());
    }

    private static MemoryStream Workbook((string Class, string Sku, string Description, string Unit, string Reference) row)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("ITEMS");
        sheet.Cell(1, 1).Value = "CLASS";
        sheet.Cell(1, 3).Value = "ITEM (Short)";
        sheet.Cell(1, 4).Value = "DESCRIPTION";
        sheet.Cell(1, 5).Value = "U/M";
        sheet.Cell(1, 12).Value = "COMPLETE PART #";
        sheet.Cell(2, 1).Value = row.Class;
        sheet.Cell(2, 3).Value = row.Sku;
        sheet.Cell(2, 4).Value = row.Description;
        sheet.Cell(2, 5).Value = row.Unit;
        sheet.Cell(2, 12).Value = row.Reference;
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private sealed class Fixture(
        WarehouseDbContext db,
        ProductImportService service,
        MutableTimeProvider clock,
        MemoryCache cache) : IAsyncDisposable
    {
        public WarehouseDbContext Db { get; } = db;
        public ProductImportService Service { get; } = service;
        public MutableTimeProvider Clock { get; } = clock;
        public Guid Owner { get; } = Guid.NewGuid();

        public static Fixture Create()
        {
            var options = new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            var db = new WarehouseDbContext(options);
            db.Database.EnsureCreated();
            var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
            var cache = new MemoryCache(new MemoryCacheOptions());
            var store = new ProductImportPreviewStore(cache, clock);
            return new Fixture(db, new ProductImportService(new ProductSpreadsheetReader(), db, store), clock, cache);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            cache.Dispose();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now = now.Add(amount);
    }
}
