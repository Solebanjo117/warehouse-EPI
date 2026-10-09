using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;

namespace WarehouseEPI.Tests.Labels;

public sealed class LabelSeriesTests
{
    internal static LabelTemplateVersion Version()
    {
        var preset = LabelTemplatePresetCatalog.RemainingExcelTemplates.Single(x => x.Code == "LBL-4X45-RECEIVING");
        return new() { Template = new() { Code = preset.Code }, Name = preset.Name, Version = 1,
            SizePreset = preset.Size, Status = LabelTemplateStatus.Published,
            DesignJson = LabelDesignSerializer.Serialize(preset.Design) };
    }
    private static readonly OperationalProductResult Product = new(Guid.NewGuid(), "SKU-1", "Product", null, "EA", false);
    private static LabelBatchResult Generate(string start, string end, int copies = 1, string field = "rollNumber") =>
        new LabelDocumentService(new BarcodeRenderingService()).RenderBatch(Version(), Product,
            new Dictionary<string, string> { [field] = start, ["yards"] = "1000" }, copies, new(field, end));

    [Theory]
    [InlineData("1", "3", 2, 3)]
    [InlineData("001", "003", 1, 3)]
    [InlineData("0", "0", 1, 1)]
    [InlineData("1", "50", 2, 50)]
    public void Series_keeps_values_and_orders_copies(string start, string end, int copies, int count)
    {
        var result = Generate(start, end, copies);
        Assert.Empty(result.Errors); Assert.Empty(result.FieldErrors);
        Assert.Equal(count, result.Documents.Count);
        Assert.Equal(count * copies, result.Documents.Sum(x => x.Copies));
        for (var index = 0; index < count; index++)
        {
            var value = (int.Parse(start) + index).ToString().PadLeft(start.Length, '0');
            Assert.Contains(result.Documents[index].Elements, x => x.Definition.Binding == "rollNumber" && x.Text == value);
            Assert.Contains(result.Documents[index].Elements, x => x.Definition.Binding == "yards" && x.Text == "1000");
        }
    }

    [Theory]
    [InlineData("1", "101", 1, "rollNumber")]
    [InlineData("1", "51", 2, "rollNumber")]
    [InlineData("3", "1", 1, "rollNumber")]
    [InlineData("", "3", 1, "rollNumber")]
    [InlineData("1", "", 1, "rollNumber")]
    [InlineData("1.5", "3", 1, "rollNumber")]
    [InlineData("ROLL-1", "3", 1, "rollNumber")]
    [InlineData("-1", "3", 1, "rollNumber")]
    [InlineData("1", "999999999999999999999999999999999999999999", 1, "rollNumber")]
    [InlineData("1", "3", 0, "rollNumber")]
    [InlineData("1", "3", 1, "product.sku")]
    [InlineData("1", "3", 1, "receivingMfgDate")]
    [InlineData("1", "3", 1, "unknown")]
    public void Invalid_series_never_returns_partial_documents(string start, string end, int copies, string field)
    {
        var result = Generate(start, end, copies, field);
        Assert.NotEmpty(result.FieldErrors); Assert.Empty(result.Documents);
    }

    [Fact]
    public void Numeric_series_normalizes_zeros_and_render_errors_discard_entire_batch()
    {
        var service = new LabelDocumentService(new BarcodeRenderingService());
        var values = new Dictionary<string, string> { ["yards"] = "001" };
        var result = service.RenderBatch(Version(), Product, values, 1, new("yards", "003"));
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "1", "2", "3" }, result.Documents.Select(x => x.Elements.Single(e => e.Definition.Binding == "yards").Text));
        values["receivingMfgDate"] = "invalid";
        result = service.RenderBatch(Version(), Product, values, 1, new("yards", "003"));
        Assert.Empty(result.Documents); Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData(LabelFieldType.Boolean)]
    [InlineData(LabelFieldType.Select)]
    [InlineData(LabelFieldType.Date)]
    public void Non_sequence_types_and_unused_fields_are_not_eligible(LabelFieldType type)
    {
        var version = Version();
        var design = LabelDesignSerializer.Deserialize(version.DesignJson)!;
        design.Fields.Single(x => x.Key == "rollNumber").Type = type;
        design.Fields.Add(new() { Key = "unused", Label = "Unused", Type = LabelFieldType.Text });
        Assert.DoesNotContain(LabelDocumentService.SeriesFields(design), x => x.Key is "rollNumber" or "unused");
    }

    [Fact]
    public void Series_updates_barcodes_and_keeps_original_input()
    {
        var version = Version();
        var design = LabelDesignSerializer.Deserialize(version.DesignJson)!;
        design.Elements.Single(x => x.Type == LabelElementType.Code128).Binding = "rollNumber";
        version.DesignJson = LabelDesignSerializer.Serialize(design);
        var values = new Dictionary<string, string> { ["rollNumber"] = "001" };
        var service = new LabelDocumentService(new BarcodeRenderingService());
        var result = service.RenderBatch(version, Product, values, 2, new("rollNumber", "003"));
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "001", "002", "003" }, result.Documents.Select(x => x.Elements.Single(e => e.Barcode is not null).Barcode!.Payload));
        Assert.Equal("001", values["rollNumber"]);
        var ordinary = service.RenderBatch(version, Product, values, 2);
        Assert.Single(ordinary.Documents); Assert.Equal(2, ordinary.Documents[0].Copies);
    }
}
