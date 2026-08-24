using WarehouseEPI.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Labels;

public sealed class LabelDesignTests
{
    [Fact]
    public void Seed_round_trips_and_uses_integer_physical_coordinates()
    {
        var seed = LabelDesignSerializer.Seed6x4();
        var json = LabelDesignSerializer.Serialize(seed);
        var restored = LabelDesignSerializer.Deserialize(json);
        var validation = LabelDesignSerializer.Validate(restored, LabelSizePreset.SixByFourLandscape);

        Assert.NotNull(restored);
        Assert.Equal(json, LabelDesignSerializer.Serialize(restored!));
        Assert.Empty(validation.Errors);
        Assert.All(restored!.Elements, element => Assert.True(element.X >= 0 && element.Y >= 0 && element.Width > 0 && element.Height > 0));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://remote.invalid/image.png")]
    [InlineData("C:\\temp\\file.png")]
    public void Configured_markup_scripts_urls_and_paths_are_rejected(string unsafeText)
    {
        var design = LabelDesignSerializer.Seed6x4();
        design.Elements[0].Text = unsafeText;
        Assert.NotEmpty(LabelDesignSerializer.Validate(design, LabelSizePreset.SixByFourLandscape).Errors);
    }

    [Fact]
    public void Unknown_bindings_duplicate_fields_and_out_of_bounds_elements_are_rejected()
    {
        var design = LabelDesignSerializer.Seed6x4();
        design.Fields =
        [
            new() { Key = "firma", Label = "Firma", Type = LabelFieldType.Text },
            new() { Key = "firma", Label = "Firma repetida", Type = LabelFieldType.Text }
        ];
        design.Elements[0].X = -1;
        design.Elements[1].Binding = "campo.desconocido";
        var result = LabelDesignSerializer.Validate(design, LabelSizePreset.SixByFourLandscape);
        Assert.Contains(result.Errors, error => error.Contains("repetida", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("fuera del lienzo", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("campo desconocido", StringComparison.Ordinal));
    }

    [Fact]
    public void Renderer_normalizes_quantity_keeps_blank_line_and_is_deterministic()
    {
        var design = LabelDesignSerializer.Seed6x4();
        design.Fields.Add(new() { Key = "inspector", Label = "Inspector", Type = LabelFieldType.Text });
        design.Elements.Add(new() { Type = LabelElementType.Field, Binding = "inspector", X = 200, Y = 3350, Width = 1700, Height = 300, BlankLine = true, ZIndex = 2 });
        var template = new LabelTemplate { Code = "TEST-LABEL" };
        var version = new LabelTemplateVersion { Template = template, Version = 3, Name = "Prueba", SizePreset = LabelSizePreset.SixByFourLandscape, Status = LabelTemplateStatus.Published, DesignJson = LabelDesignSerializer.Serialize(design) };
        var product = new OperationalProductResult(Guid.NewGuid(), "SKU-123", "Producto", "REF-1", "LB", true);
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["input.quantity"] = "2.5000", ["input.manufacturingDate"] = "2026-08-24", ["input.isRepack"] = "true", ["inspector"] = "" };
        var service = new LabelDocumentService(new BarcodeRenderingService());

        var first = service.Render(version, product, values, 2);
        var second = service.Render(version, product, values, 2);

        Assert.Empty(first.Errors);
        Assert.Equal(2, first.Document!.Copies);
        Assert.Equal(first.Document.Elements.Select(item => item.Barcode?.Markup), second.Document!.Elements.Select(item => item.Barcode?.Markup));
        Assert.Contains(first.Document.Elements, item => item.Definition.Binding == "input.quantity" && item.Barcode?.Payload == "2.5");
        Assert.Contains(first.Document.Elements, item => item.Definition.Binding == "inspector" && item.Text == string.Empty && item.Definition.BlankLine);
    }

    [Fact]
    public void Renderer_rejects_unknown_fields_invalid_types_and_non_decimal_units()
    {
        var design = LabelDesignSerializer.Seed6x4();
        design.Fields.Add(new() { Key = "turno", Label = "Turno", Type = LabelFieldType.Select, Required = true, Options = ["A", "B"] });
        var template = new LabelTemplate { Code = "TEST" };
        var version = new LabelTemplateVersion { Template = template, Version = 1, Name = "Prueba", SizePreset = LabelSizePreset.SixByFourLandscape, DesignJson = LabelDesignSerializer.Serialize(design) };
        var product = new OperationalProductResult(Guid.NewGuid(), "SKU", null, null, "EA", false);
        var values = new Dictionary<string, string> { ["input.quantity"] = "1.5", ["input.manufacturingDate"] = "bad", ["turno"] = "C", ["intruso"] = "x" };
        var result = new LabelDocumentService(new BarcodeRenderingService()).Render(version, product, values, 101);
        Assert.Null(result.Document);
        Assert.Contains(result.Errors, error => error.Contains("copias", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("no admite decimales", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("opción inválida", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("no pertenece", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Asset_upload_validates_content_dimensions_and_deduplicates_by_sha256()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WarehouseDbContext(options);
        var service = new LabelAssetService(db, TimeProvider.System);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        var first = await service.UploadAsync(Guid.NewGuid(), "firma.png", "image/png", png);
        var duplicate = await service.UploadAsync(Guid.NewGuid(), "otra.png", "image/png", png);
        var invalid = await service.UploadAsync(Guid.NewGuid(), "rota.png", "image/png", png[..24]);

        Assert.Null(first.Error);
        Assert.Equal(1, first.Asset?.Width);
        Assert.Equal(first.Asset?.Id, duplicate.Asset?.Id);
        Assert.NotNull(invalid.Error);
        Assert.Single(await db.LabelAssets.ToListAsync());
    }
}
