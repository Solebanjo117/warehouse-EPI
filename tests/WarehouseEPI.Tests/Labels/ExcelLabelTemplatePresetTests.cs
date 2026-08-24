using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;

namespace WarehouseEPI.Tests.Labels;

public sealed class ExcelLabelTemplatePresetTests
{
    private static readonly string[] ExpectedCodes =
    [
        "LBL-4X6-STANDARD", "LBL-3X1-COMPACT", "LBL-6X4-PARTIAL",
        "LBL-6X4-SPOUTED", "LBL-6X4-BERM", "LBL-6X4-RAINCAP",
        "LBL-6X4-CUSTOM-SPOUT", "LBL-6X4-MOBILE", "LBL-4X45-RECEIVING"
    ];

    [Fact]
    public void Remaining_excel_presets_are_deterministic_unique_and_valid()
    {
        var first = LabelTemplatePresetCatalog.RemainingExcelTemplates;
        var second = LabelTemplatePresetCatalog.RemainingExcelTemplates;

        Assert.Equal(ExpectedCodes, first.Select(item => item.Code));
        Assert.Equal(9, first.Select(item => item.TemplateId).Distinct().Count());
        Assert.Equal(9, first.Select(item => item.VersionId).Distinct().Count());
        Assert.Equal(9, first.Select(item => item.EventId).Distinct().Count());
        Assert.Equal(first.Select(item => LabelDesignSerializer.Serialize(item.Design)),
            second.Select(item => LabelDesignSerializer.Serialize(item.Design)));

        foreach (var preset in first)
        {
            var validation = LabelDesignSerializer.Validate(preset.Design, preset.Size);
            Assert.True(validation.IsValid, $"{preset.Code}: {string.Join(" | ", validation.Errors)}");
            Assert.Empty(validation.Warnings);
            Assert.All(preset.Design.Elements, element => Assert.Equal("Arial", element.FontFamily));
        }
    }

    [Fact]
    public void Every_preset_renders_with_exact_size_and_local_code128()
    {
        var renderer = new LabelDocumentService(new BarcodeRenderingService());
        var product = new OperationalProductResult(Guid.NewGuid(), "SKU-LARGO-1234567890", "Descripción de prueba", "EXT-9", "EA", false);

        foreach (var preset in LabelTemplatePresetCatalog.RemainingExcelTemplates)
        {
            var template = new LabelTemplate { Code = preset.Code };
            var version = new LabelTemplateVersion
            {
                Template = template,
                Version = 1,
                Name = preset.Name,
                SizePreset = preset.Size,
                Status = LabelTemplateStatus.Published,
                DesignJson = LabelDesignSerializer.Serialize(preset.Design)
            };
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["input.quantity"] = "2",
                ["input.manufacturingDate"] = "2026-08-24",
                ["input.isRepack"] = "false"
            };
            foreach (var field in preset.Design.Fields) values[field.Key] = string.Empty;

            var result = renderer.Render(version, product, values, 2);

            Assert.Empty(result.Errors);
            Assert.Equal(LabelSizeRegistry.Get(preset.Size), result.Document!.Size);
            Assert.Equal(2, result.Document.Copies);
            Assert.Contains(result.Document.Elements,
                item => item.Definition.Binding == "product.sku" && item.Barcode?.Payload == product.Sku);
        }
    }

    [Fact]
    public void Special_fields_support_computer_values_or_blank_handwritten_lines()
    {
        var preset = Assert.Single(LabelTemplatePresetCatalog.RemainingExcelTemplates,
            item => item.Code == "LBL-6X4-SPOUTED");
        var template = new LabelTemplate { Code = preset.Code };
        var version = new LabelTemplateVersion
        {
            Template = template,
            Version = 1,
            Name = preset.Name,
            SizePreset = preset.Size,
            Status = LabelTemplateStatus.Published,
            DesignJson = LabelDesignSerializer.Serialize(preset.Design)
        };
        var product = new OperationalProductResult(Guid.NewGuid(), "SPOUT-1", "Bolsa con boquilla", null, "EA", false);
        var renderer = new LabelDocumentService(new BarcodeRenderingService());
        var emptyValues = preset.Design.Fields.ToDictionary(field => field.Key, _ => string.Empty, StringComparer.Ordinal);

        var empty = renderer.Render(version, product, emptyValues, 1);
        var typed = renderer.Render(version, product, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cutPanel"] = "JP",
            ["length"] = "8 ft",
            ["mfd"] = "2026-08-24"
        }, 1);

        Assert.Empty(empty.Errors);
        Assert.Contains(empty.Document!.Elements,
            item => item.Definition.Binding == "cutPanel" && item.Text == string.Empty && item.Definition.BlankLine);
        Assert.Empty(typed.Errors);
        Assert.Contains(typed.Document!.Elements, item => item.Definition.Binding == "cutPanel" && item.Text == "JP");
        Assert.Contains(typed.Document.Elements, item => item.Definition.Binding == "length" && item.Text == "8 ft");
        Assert.Contains(typed.Document.Elements, item => item.Definition.Binding == "mfd" && item.Text == "08/24/2026");
    }

    [Fact]
    public void Custom_spout_defaults_are_editable_and_numbers_are_normalized()
    {
        var preset = Assert.Single(LabelTemplatePresetCatalog.RemainingExcelTemplates,
            item => item.Code == "LBL-6X4-CUSTOM-SPOUT");
        var template = new LabelTemplate { Code = preset.Code };
        var version = new LabelTemplateVersion
        {
            Template = template,
            Version = 1,
            Name = preset.Name,
            SizePreset = preset.Size,
            DesignJson = LabelDesignSerializer.Serialize(preset.Design)
        };
        var product = new OperationalProductResult(Guid.NewGuid(), "CUSTOM-SPOUT", "Bolsa especial", null, "EA", false);
        var renderer = new LabelDocumentService(new BarcodeRenderingService());

        var defaults = renderer.Render(version, product, new Dictionary<string, string>(), 1);
        var edited = renderer.Render(version, product, new Dictionary<string, string>
        {
            ["spoutCount"] = "3.0000",
            ["spoutDiameter"] = "18.5000"
        }, 1);

        Assert.Contains(defaults.Document!.Elements, item => item.Definition.Binding == "spoutCount" && item.Text == "2");
        Assert.Contains(defaults.Document.Elements, item => item.Definition.Binding == "spoutDiameter" && item.Text == "24");
        Assert.Contains(edited.Document!.Elements, item => item.Definition.Binding == "spoutCount" && item.Text == "3");
        Assert.Contains(edited.Document.Elements, item => item.Definition.Binding == "spoutDiameter" && item.Text == "18.5");
    }
}
