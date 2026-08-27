using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Labels;

internal sealed record PalletLicensePlatePreset(Guid TemplateId, Guid VersionId, Guid EventId, string Code,
    string Name, LabelSizePreset Size, LabelDesignDocumentV1 Design);

internal static class PalletLicensePlatePresetCatalog
{
    internal static PalletLicensePlatePreset Initial => new(
        Guid.Parse("71000000-0000-0000-0000-000000000001"),
        Guid.Parse("71000000-0000-0000-0000-000000000002"),
        Guid.Parse("71000000-0000-0000-0000-000000000003"),
        "PLT-LICENSE-PLATE", "Pallet License Plate", LabelSizePreset.ElevenByEightPointFiveLandscape,
        new LabelDesignDocumentV1
        {
            Fields = [new() { Key = "weight", Label = "Peso", Help = "Opcional; máximo 40 caracteres.", Type = LabelFieldType.Text, Required = false }],
            Elements =
            [
                Image(1, 250, 220, 3200, 540),
                Text(2, 6900, 220, 3600, 260, "PALLET LICENSE PLATE", 18, true, "right"),
                Field(3, 6900, 510, 3600, 220, "plate.identifier", 10, true, "right"),
                Text(4, 250, 1050, 900, 210, "ITEM", 14, true),
                Barcode(5, 1250, 900, 9000, 850, "product.sku"),
                Field(6, 250, 1810, 10000, 430, "product.sku", 26, true, "center"),
                Text(7, 250, 2450, 1700, 210, "DESCRIPTION", 13, true),
                Field(8, 250, 2700, 7600, 1100, "product.description", 19, true, "center"),
                Text(9, 8150, 2450, 1900, 210, "DESTINATION", 13, true),
                Field(10, 8150, 2700, 1900, 600, "entry.destination", 15, true, "center"),
                Text(11, 8150, 3400, 1900, 180, "REFERENCE", 11, true),
                Field(12, 8150, 3600, 1900, 200, "entry.reference", 11, false, "center", true),
                Text(13, 250, 4250, 1200, 220, "DATE", 14, true),
                Field(14, 250, 4500, 2100, 440, "entry.occurredDate", 20, true, "center"),
                Text(15, 2700, 4250, 1000, 220, "QTY", 14, true),
                Field(16, 2700, 4500, 1700, 440, "entry.quantity", 24, true, "center"),
                Text(17, 4650, 4250, 1000, 220, "UNIT", 14, true),
                Field(18, 4650, 4500, 1300, 440, "entry.unit", 20, true, "center"),
                Text(19, 6400, 4250, 1200, 220, "WEIGHT", 14, true),
                Field(20, 6400, 4500, 2000, 440, "weight", 20, true, "center", true),
                Text(21, 250, 5450, 1600, 210, "RECEIVED", 14, true),
                Field(22, 2050, 5450, 2100, 250, "entry.occurredDate", 14, true, "center"),
                Text(23, 4450, 5450, 600, 210, "BY", 14, true),
                Field(24, 5150, 5450, 3000, 250, "entry.responsible", 14, true, "center"),
                Text(25, 250, 6250, "□  COUNTED", 15),
                Line(26, 2100, 6550, 3000),
                Text(27, 5700, 6250, "□  REMOVED", 15),
                Line(28, 7600, 6550, 2450),
                Text(29, 8350, 7920, "PLT-LICENSE-PLATE · v1", 8, true, "right")
            ]
        });

    private static LabelElementDefinition Text(int id, int x, int y, string value, int size, bool bold = false, string align = "left") =>
        new() { Id = Id(id), Type = LabelElementType.Text, X = x, Y = y, Width = 1800, Height = 240, Text = value, FontSize = size, Bold = bold, Align = align, ZIndex = 1 };
    private static LabelElementDefinition Text(int id, int x, int y, int width, int height, string value, int size, bool bold = false, string align = "left") =>
        new() { Id = Id(id), Type = LabelElementType.Text, X = x, Y = y, Width = width, Height = height, Text = value, FontSize = size, Bold = bold, Align = align, ZIndex = 1 };
    private static LabelElementDefinition Field(int id, int x, int y, int width, int height, string binding, int size, bool bold, string align, bool blankLine = false) =>
        new() { Id = Id(id), Type = LabelElementType.Field, X = x, Y = y, Width = width, Height = height, Binding = binding, FontSize = size, Bold = bold, Align = align, BlankLine = blankLine, ZIndex = 1 };
    private static LabelElementDefinition Barcode(int id, int x, int y, int width, int height, string binding) =>
        new() { Id = Id(id), Type = LabelElementType.Code128, X = x, Y = y, Width = width, Height = height, Binding = binding, ZIndex = 1 };
    private static LabelElementDefinition Image(int id, int x, int y, int width, int height) =>
        new() { Id = Id(id), Type = LabelElementType.Image, X = x, Y = y, Width = width, Height = height, BuiltInAssetKey = "extra-packaging-logo", ZIndex = 1 };
    private static LabelElementDefinition Line(int id, int x, int y, int width) =>
        new() { Id = Id(id), Type = LabelElementType.Line, X = x, Y = y, Width = width, Height = 20, ZIndex = 1 };
    private static Guid Id(int id) => Guid.Parse($"72000000-0000-0000-0000-{id:D12}");
}
