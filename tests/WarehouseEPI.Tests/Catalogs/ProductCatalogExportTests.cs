using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Catalogs;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Catalogs;

public sealed class ProductCatalogExportTests
{
    internal static WarehouseDbContext Context() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase("product-export-" + Guid.NewGuid()).Options);

    [Fact]
    public async Task Complete_catalogue_has_exact_six_columns_including_inactive_nulls_and_units()
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var unit = await db.Units.SingleAsync(x => x.Id == 1);
        unit.IsActive = false;
        var productClass = new ProductClass { Code = "0008", Name = "Export class", IsActive = false };
        db.Add(productClass);
        for (var i = 0; i < 32; i++) db.Add(new Product { Sku = $"000{i:000}", BaseUnitId = 1,
            ProductClass = productClass, IsActive = i % 2 == 0, Description = "Descripción, con acento", ExternalReference = $"REF-{i}" });
        db.Add(new Product { Sku = "UNASSIGNED-ITEM", BaseUnitId = 18, IsActive = false });
        await db.SaveChangesAsync();
        using var workbook = new XLWorkbook(new MemoryStream(await new ProductCatalogExportService(db).ExportAsync()));
        var sheet = Assert.Single(workbook.Worksheets);
        Assert.Equal("ITEM LISTING", sheet.Name);
        Assert.Equal(new[] { "CLASS", "PACKED BY (AREA)", "ITEM (Short)", "DESCRIPTION", "U/M", "ITEM (COMPLETE)" },
            Enumerable.Range(1, 6).Select(column => sheet.Cell(1, column).GetString()));
        Assert.Equal(6, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(34, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal("0008", sheet.Cell(2, 1).GetString());
        Assert.Equal("000000", sheet.Cell(2, 3).GetString());
        Assert.Equal(XLDataType.Text, sheet.Cell(2, 3).DataType);
        Assert.Equal($"{unit.Name} ({unit.Code})", sheet.Cell(2, 5).GetString());
        Assert.Equal("REF-0", sheet.Cell(2, 6).GetString());
        Assert.All(Enumerable.Range(2, 33), row => Assert.True(sheet.Cell(row, 2).IsEmpty()));
        foreach (var column in new[] { 1, 2, 4, 5, 6 }) Assert.True(sheet.Cell(34, column).IsEmpty());
        Assert.True(sheet.AutoFilter.IsEnabled);
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.Equal(33, await db.Products.CountAsync());
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.invalid\")", true)]
    [InlineData("+00001", true)]
    [InlineData("-00001", true)]
    [InlineData("@SUM(A1)", true)]
    [InlineData("\t=1+1", true)]
    [InlineData("'00001", false)]
    [InlineData("''00001", false)]
    [InlineData("00001", false)]
    [InlineData("ÁRBOL, \"A\"\nSEGUNDA LÍNEA", false)]
    public async Task Text_preserves_exact_content_and_never_becomes_a_formula(string value, bool protectedText)
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var productClass = new ProductClass { Code = value, Name = "Text test" };
        db.Add(new Product { Sku = value, Description = value, ExternalReference = value, BaseUnitId = 1, ProductClass = productClass });
        await db.SaveChangesAsync();
        using var workbook = new XLWorkbook(new MemoryStream(await new ProductCatalogExportService(db).ExportAsync()));
        foreach (var column in new[] { 1, 3, 4, 6 })
        {
            var cell = workbook.Worksheet(1).Cell(2, column);
            Assert.Equal(value, cell.GetString());
            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.False(cell.HasFormula);
            if (protectedText) Assert.True(cell.Style.IncludeQuotePrefix);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ProductCatalogExportService.MaxRows)]
    [InlineData(ProductCatalogExportService.MaxRows + 1)]
    public async Task Empty_and_limit_are_explicit_without_silent_truncation(int count)
    {
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        db.AddRange(Enumerable.Range(0, count).Select(i => new Product { Sku = $"EXPORT-{i:00000}", BaseUnitId = 1 }));
        await db.SaveChangesAsync();
        var service = new ProductCatalogExportService(db);
        if (count > ProductCatalogExportService.MaxRows)
            Assert.Equal(ProductCatalogExportService.LimitMessage,
                (await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync())).Message);
        else
        {
            using var workbook = new XLWorkbook(new MemoryStream(await service.ExportAsync()));
            Assert.Equal(count + 1, workbook.Worksheet(1).LastRowUsed()!.RowNumber());
        }
    }
}
