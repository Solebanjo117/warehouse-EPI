using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
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

    [Fact]
    public async Task Comparison_updates_only_basic_fields_and_preserves_blanks_and_inactive_state()
    {
        await using var fixture = Fixture.Create();
        var product = new Product
        {
            Sku = "EXISTING",
            Description = "Antes",
            ExternalReference = "KEEP",
            BaseUnitId = 1,
            MinimumStock = 12,
            IsActive = false
        };
        fixture.Db.Products.Add(product);
        await fixture.Db.SaveChangesAsync();
        using var stream = Workbook(("", " existing ", "Después", "", ""));
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting: true);
        Assert.Equal("Antes", product.Description);
        Assert.Equal(1, preview.UpdatedCount);
        var change = Assert.Single(Assert.Single(preview.Rows).Changes);
        Assert.Equal("Antes", change.Before);
        Assert.Equal("Después", change.After);
        var result = await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Updated);
        Assert.Equal("Después", product.Description);
        Assert.Equal("KEEP", product.ExternalReference);
        Assert.Equal(1, product.BaseUnitId);
        Assert.Equal(12, product.MinimumStock);
        Assert.False(product.IsActive);
    }

    [Fact]
    public async Task Comparison_rejects_stale_values_without_overwriting()
    {
        await using var fixture = Fixture.Create();
        var product = new Product { Sku = "EXISTING", Description = "Antes", BaseUnitId = 1 };
        fixture.Db.Products.Add(product);
        await fixture.Db.SaveChangesAsync();
        using var stream = Workbook(("RM", "EXISTING", "Excel", "Each (EA)", ""));
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting: true);
        product.Description = "Edición concurrente";
        await fixture.Db.SaveChangesAsync();
        Assert.False((await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner)).Succeeded);
        Assert.Equal("Edición concurrente", product.Description);
    }

    [Fact]
    public async Task Comparison_applies_valid_rows_while_skipping_conflicting_duplicates()
    {
        await using var fixture = Fixture.Create();
        using var original = Workbook(("RM", "DUP", "Uno", "Each (EA)", "FIRST"));
        using var book = new XLWorkbook(original);
        var sheet = book.Worksheet(1);
        sheet.Name = "ITEM LISTING";
        sheet.Cell(1, 12).Value = "ITEM (COMPLETE)";
        sheet.Row(2).CopyTo(sheet.Row(3));
        sheet.Cell(3, 12).Value = "CONFLICT";
        sheet.Row(2).CopyTo(sheet.Row(4));
        sheet.Cell(4, 3).Value = "VALID";
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        stream.Position = 0;
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting: true);
        Assert.True(preview.CanConfirm);
        Assert.Equal(1, preview.NewCount);
        Assert.Contains(preview.Issues, x => x.Code == "duplicate_conflict");
        Assert.True((await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner)).Succeeded);
        Assert.Equal("VALID", (await fixture.Db.Products.SingleAsync()).Sku);
    }

    [Fact]
    public async Task Comparison_without_changes_does_not_offer_confirmation()
    {
        await using var fixture = Fixture.Create();
        fixture.Db.Products.Add(new Product { Sku = "SAME", Description = "Same", BaseUnitId = 1 });
        await fixture.Db.SaveChangesAsync();
        using var stream = Workbook(("", "SAME", "Same", "", ""));
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting: true);
        Assert.Equal(1, preview.UnchangedCount);
        Assert.False(preview.CanConfirm);
    }

    [Fact]
    public async Task Comparison_skips_unit_change_with_movements_and_rechecks_before_apply()
    {
        await using var fixture = Fixture.Create();
        var product = new Product { Sku = "MOVED", BaseUnitId = 1 };
        fixture.Db.Products.Add(product);
        fixture.Db.Units.Add(new Unit { Id = 100, Code = "TEST", Name = "Test", IsActive = true });
        await fixture.Db.SaveChangesAsync();
        using var stream = Workbook(("", "MOVED", "Change", "Test (TEST)", ""));
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting: true);
        Assert.Equal(1, preview.UpdatedCount);
        fixture.Db.InventoryMovementLines.Add(new InventoryMovementLine { ProductId = product.Id, UnitId = 1, Quantity = 1 });
        await fixture.Db.SaveChangesAsync();
        Assert.False((await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner)).Succeeded);
        Assert.Equal(1, product.BaseUnitId);
        stream.Position = 0;
        var second = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting: true);
        Assert.True(Assert.Single(second.Rows).HasError);
        Assert.False(second.CanConfirm);
    }

    [Fact]
    public async Task Comparison_defaults_new_product_with_blank_unit_to_unassigned()
    {
        await using var fixture = Fixture.Create();
        using var stream = Workbook(("", "NEW", "", "", ""));
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting: true);
        Assert.False(Assert.Single(preview.Rows).HasError);
        Assert.True(preview.CanConfirm);
        Assert.True((await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner)).Succeeded);
        var product = await fixture.Db.Products.Include(x => x.BaseUnit).SingleAsync();
        Assert.Equal(CatalogDefaults.UnassignedUnitCode, product.BaseUnit.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_class_is_previewed_then_created_once_for_multiple_products(bool updateExisting)
    {
        await using var fixture = Fixture.Create();
        using var original = Workbook((" new class ", "ONE", "One", "", ""));
        using var book = new XLWorkbook(original);
        book.Worksheet(1).Row(2).CopyTo(book.Worksheet(1).Row(3));
        book.Worksheet(1).Cell(3, 3).Value = "TWO";
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        stream.Position = 0;
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting);
        Assert.True(preview.CanConfirm);
        Assert.All(preview.Rows, x => Assert.True(x.IsNewClass));
        Assert.False(await fixture.Db.ProductClasses.AnyAsync(x => x.Code == "NEW CLASS"));
        Assert.True((await fixture.Service.ConfirmAsync(preview.Token, fixture.Owner)).Succeeded);
        var created = await fixture.Db.ProductClasses.SingleAsync(x => x.Code == "NEW CLASS");
        Assert.Equal("NEW CLASS", created.Name);
        Assert.True(created.IsActive);
        Assert.All(await fixture.Db.Products.ToListAsync(), x => Assert.Equal(created.Id, x.ProductClassId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inactive_class_is_not_recreated_or_reactivated(bool updateExisting)
    {
        await using var fixture = Fixture.Create();
        var productClass = await fixture.Db.ProductClasses.SingleAsync(x => x.Code == "RM");
        productClass.IsActive = false;
        await fixture.Db.SaveChangesAsync();
        using var stream = Workbook(("RM", "NEW", "", "", ""));
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting);
        Assert.True(Assert.Single(preview.Rows).HasError);
        Assert.False(preview.CanConfirm);
        Assert.False(productClass.IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unit_selection_rebuilds_preview_and_requires_owner_and_active_catalog(bool updateExisting)
    {
        await using var fixture = Fixture.Create();
        using var stream = Workbook(("RM", "SQFT-ROW", "Area", "Sq. Foot:SQFT", ""));
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting);
        Assert.Contains("Sq. Foot:SQFT", preview.UnresolvedUnits);
        Assert.False(preview.CanConfirm);
        Assert.Null(await fixture.Service.ResolveUnitAsync(preview.Token, Guid.NewGuid(), "Sq. Foot:SQFT", "EA", default));
        Assert.Null(await fixture.Service.ResolveUnitAsync(preview.Token, fixture.Owner, "Sq. Foot:SQFT", "UNKNOWN", default));
        var revised = await fixture.Service.ResolveUnitAsync(preview.Token, fixture.Owner, "Sq. Foot:SQFT", "EA", default);
        Assert.NotNull(revised);
        Assert.True(revised.CanConfirm);
        Assert.Empty(revised.UnresolvedUnits);
        Assert.Empty(fixture.Db.Products);
        Assert.False(fixture.Service.TryGetPreview(preview.Token, fixture.Owner, out _));
        Assert.True((await fixture.Service.ConfirmAsync(revised.Token, fixture.Owner)).Succeeded);
        Assert.Equal(1, (await fixture.Db.Products.SingleAsync()).BaseUnitId);
    }

    [Fact]
    public async Task Complementary_duplicate_fields_are_merged_in_item_listing()
    {
        await using var fixture = Fixture.Create();
        using var original = Workbook(("", "DUP", "", "", "REF"));
        using var book = new XLWorkbook(original);
        var sheet = book.Worksheet(1);
        sheet.Name = "ITEM LISTING";
        sheet.Cell(1, 12).Value = "ITEM (COMPLETE)";
        sheet.Row(2).CopyTo(sheet.Row(3));
        sheet.Cell(3, 1).Value = "RM";
        sheet.Cell(3, 4).Value = "Description";
        sheet.Cell(3, 5).Value = "Each (EA)";
        sheet.Cell(3, 12).Value = "";
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        stream.Position = 0;
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, true);
        var row = Assert.Single(preview.Rows);
        Assert.Equal("Description", row.Description);
        Assert.Equal("REF", row.ExternalReference);
        Assert.Equal("RM", row.ClassCode);
        Assert.Equal("EA", row.UnitCode);
        Assert.Equal(new[] { 2, 3 }, row.SourceRows);
        Assert.True(preview.CanConfirm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task User_can_resolve_duplicate_reference_without_writing_until_confirmation(bool updateExisting)
    {
        await using var fixture = Fixture.Create();
        using var original = Workbook(("RM", "PBAG-BBAG-XS", "Description", "Each (EA)", "PBAG-BBAG-XS"));
        using var book = new XLWorkbook(original);
        var sheet = book.Worksheet(1);
        sheet.Row(2).CopyTo(sheet.Row(3));
        sheet.Cell(3, 4).Value = "";
        sheet.Cell(3, 12).Value = "YY-RM-BBAG:PBAG-BBAG-XS";
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        stream.Position = 0;
        var preview = await fixture.Service.PrepareAsync(stream, "products.xlsx", fixture.Owner, updateExisting);
        Assert.Single(preview.Source!.Conflicts);
        var choices = new Dictionary<string, string> { ["Referencia completa"] = "YY-RM-BBAG:PBAG-BBAG-XS" };
        Assert.Null(await fixture.Service.ResolveDuplicateAsync(preview.Token, Guid.NewGuid(), "PBAG-BBAG-XS", choices, default));
        Assert.Null(await fixture.Service.ResolveDuplicateAsync(preview.Token, fixture.Owner, "PBAG-BBAG-XS",
            new Dictionary<string, string> { ["Referencia completa"] = "FORGED" }, default));
        var revised = await fixture.Service.ResolveDuplicateAsync(preview.Token, fixture.Owner, "PBAG-BBAG-XS", choices, default);
        Assert.NotNull(revised);
        Assert.Empty(revised.Source!.Conflicts);
        Assert.True(revised.CanConfirm);
        Assert.Equal("Description", Assert.Single(revised.Rows).Description);
        Assert.Equal("YY-RM-BBAG:PBAG-BBAG-XS", revised.Rows[0].ExternalReference);
        Assert.Empty(fixture.Db.Products);
        Assert.False(fixture.Service.TryGetPreview(preview.Token, fixture.Owner, out _));
        Assert.True((await fixture.Service.ConfirmAsync(revised.Token, fixture.Owner)).Succeeded);
        Assert.Equal("YY-RM-BBAG:PBAG-BBAG-XS", (await fixture.Db.Products.SingleAsync()).ExternalReference);
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
