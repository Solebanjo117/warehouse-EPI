using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Labels;

internal sealed record LabelTemplatePresetSeed(
    Guid TemplateId,
    Guid VersionId,
    Guid EventId,
    string Code,
    string Name,
    LabelSizePreset Size,
    string SourceSheet,
    LabelDesignDocumentV1 Design);

internal static class LabelTemplatePresetCatalog
{
    private const string OptionalHelp = "Opcional; déjalo vacío para escribirlo a mano.";

    internal static IReadOnlyList<LabelTemplatePresetSeed> RemainingExcelTemplates =>
    [
        Standard4x6(),
        Compact3x1(),
        Partial6x4(),
        Spouted6x4(),
        Berm6x4(),
        Raincap6x4(),
        CustomSpout6x4(),
        Mobile6x4(),
        Receiving4x45()
    ];

    private static LabelTemplatePresetSeed Standard4x6()
    {
        const int template = 1;
        return Seed(template, "LBL-4X6-STANDARD", "Caja estándar 4×6", LabelSizePreset.FourBySixPortrait,
            "4X6 SEARCH BY DESCRIPTION / 4X6 SEARCH BY ITEM",
            new()
            {
                Elements =
                [
                    Text(template, 1, 160, 160, 900, 180, "PART NO.", 10, true),
                    Barcode(template, 2, 160, 380, 3680, 780, "product.sku"),
                    Field(template, 3, 160, 1210, 3680, 500, "product.sku", 22, true, "center"),
                    Text(template, 4, 160, 1820, 1300, 180, "DESCRIPTION", 10, true),
                    Field(template, 5, 160, 2040, 3680, 1660, "product.description", 18, true, "center"),
                    Text(template, 6, 160, 3870, 500, 180, "QTY", 10, true),
                    Barcode(template, 7, 160, 4100, 2200, 620, "input.quantity"),
                    Field(template, 8, 2400, 4100, 1440, 620, "input.quantity", 18, true, "center"),
                    Text(template, 9, 160, 4920, 900, 180, "DATE MFG", 10, true),
                    Field(template, 10, 160, 5170, 2000, 450, "input.manufacturingDate", 14, true),
                    Text(template, 11, 2400, 4920, 900, 180, "REPACK", 10, true),
                    Field(template, 12, 2400, 5170, 1440, 450, "input.isRepack", 16, true, "center"),
                    Mark(template, 13, "LBL-4X6-STANDARD · v1", 2700, 5700, 1140)
                ]
            });
    }

    private static LabelTemplatePresetSeed Compact3x1()
    {
        const int template = 2;
        return Seed(template, "LBL-3X1-COMPACT", "Compacta 3×1", LabelSizePreset.ThreeByOneLandscape,
            "3X1 SEARCH BY ITEM",
            new()
            {
                Elements =
                [
                    Barcode(template, 1, 160, 160, 1450, 280, "product.sku"),
                    Field(template, 2, 160, 470, 1450, 180, "product.sku", 8, true, "center"),
                    Text(template, 3, 1700, 160, 280, 140, "QTY", 7, true),
                    Field(template, 4, 1980, 160, 880, 180, "input.quantity", 9, true, "center"),
                    Text(template, 5, 1700, 410, 360, 140, "MFG", 7, true),
                    Field(template, 6, 2060, 410, 800, 160, "input.manufacturingDate", 7, true, "center"),
                    Text(template, 7, 1700, 650, 420, 140, "REPACK", 7, true),
                    Field(template, 8, 2120, 650, 740, 160, "input.isRepack", 8, true, "center")
                ]
            });
    }

    private static LabelTemplatePresetSeed Partial6x4()
    {
        const int template = 3;
        return Seed(template, "LBL-6X4-PARTIAL", "Caja parcial 6×4", LabelSizePreset.SixByFourLandscape,
            "6X4 PARTIAL / 6X4 PARTIAL (2)",
            new()
            {
                Elements =
                [
                    Text(template, 1, 160, 160, 5680, 430, "PARTIAL", 34, true, "center"),
                    Barcode(template, 2, 160, 700, 5680, 650, "product.sku"),
                    Field(template, 3, 160, 1400, 5680, 430, "product.sku", 23, true, "center"),
                    Text(template, 4, 160, 2010, 500, 180, "QTY", 11, true),
                    Barcode(template, 5, 160, 2240, 2500, 630, "input.quantity"),
                    Field(template, 6, 2800, 2240, 1200, 630, "input.quantity", 22, true, "center"),
                    Text(template, 7, 160, 3100, 900, 180, "DATE MFG", 11, true),
                    Field(template, 8, 160, 3340, 2200, 420, "input.manufacturingDate", 16, true),
                    Text(template, 9, 3400, 3100, 900, 180, "REPACK", 11, true),
                    Field(template, 10, 3400, 3340, 1600, 420, "input.isRepack", 18, true, "center"),
                    Mark(template, 11, "LBL-6X4-PARTIAL · v1", 4500, 3680, 1340)
                ]
            });
    }

    private static LabelTemplatePresetSeed Spouted6x4()
    {
        const int template = 4;
        var fields = new List<LabelFieldDefinition>
        {
            OptionalText("cutPanel", "Cut Panel"), OptionalText("sewing", "Sewing"),
            OptionalText("inspector", "Inspector"), OptionalText("packing", "Packing"),
            OptionalText("length", "Length"), OptionalText("height", "Height"),
            OptionalText("width", "Width"), OptionalText("spouts", "Spouts"), OptionalDate("mfd", "MFD")
        };
        var elements = ProductionHeader(template, "LBL-6X4-SPOUTED");
        AddColumn(elements, template, 7, 160, 2200, ["cutPanel", "sewing", "inspector", "packing"], fields);
        AddColumn(elements, template, 15, 2500, 1500, ["length", "height", "width"], fields);
        AddColumn(elements, template, 21, 4200, 1640, ["spouts", "mfd"], fields);
        return Seed(template, "LBL-6X4-SPOUTED", "Spouted Bags 6×4", LabelSizePreset.SixByFourLandscape,
            "SPOUTED BAGS 6X4", new() { Fields = fields, Elements = elements });
    }

    private static LabelTemplatePresetSeed Berm6x4()
    {
        const int template = 5;
        var fields = new List<LabelFieldDefinition>
        {
            OptionalText("cutPanel", "Cut Panel"), OptionalText("weld", "Weld"),
            OptionalText("inspector", "Inspector"), OptionalText("packing", "Packing"),
            OptionalText("length", "Length"), OptionalText("width", "Width"),
            OptionalText("height", "Height"), OptionalText("bracketSlots", "Bracket Slots"), OptionalDate("mfd", "MFD")
        };
        var elements = ProductionHeader(template, "LBL-6X4-BERM");
        AddColumn(elements, template, 7, 160, 2200, ["cutPanel", "weld", "inspector", "packing"], fields);
        AddColumn(elements, template, 15, 2500, 1500, ["length", "width", "height"], fields);
        AddColumn(elements, template, 21, 4200, 1640, ["bracketSlots", "mfd"], fields);
        return Seed(template, "LBL-6X4-BERM", "Berms 6×4", LabelSizePreset.SixByFourLandscape,
            "BERMS 6X4", new() { Fields = fields, Elements = elements });
    }

    private static LabelTemplatePresetSeed Raincap6x4()
    {
        const int template = 6;
        var fields = new List<LabelFieldDefinition>
        {
            OptionalText("cutPanel", "Cut Panel"), OptionalText("sewing", "Sewing"),
            OptionalText("inspector", "Inspector"), OptionalText("packing", "Packing"),
            OptionalText("length", "Length"), OptionalText("width", "Width"), OptionalDate("mfd", "MFD")
        };
        var elements = ProductionHeader(template, "LBL-6X4-RAINCAP");
        AddColumn(elements, template, 7, 160, 2200, ["cutPanel", "sewing", "inspector", "packing"], fields);
        AddColumn(elements, template, 15, 3000, 2200, ["length", "width", "mfd"], fields);
        return Seed(template, "LBL-6X4-RAINCAP", "Raincaps 6×4", LabelSizePreset.SixByFourLandscape,
            "RAINCAPS 6X4", new() { Fields = fields, Elements = elements });
    }

    private static LabelTemplatePresetSeed CustomSpout6x4()
    {
        const int template = 7;
        var fields = new List<LabelFieldDefinition>
        {
            OptionalText("cutPanel", "Cut Panel"), OptionalText("sewing", "Sewing"),
            OptionalText("inspector", "Inspector"), OptionalText("packing", "Packing"),
            OptionalText("length", "Length"), OptionalText("height", "Height"), OptionalText("width", "Width"),
            OptionalNumber("spoutCount", "Spouts", "2"), OptionalNumber("spoutDiameter", "Spout Diameter (in)", "24"), OptionalDate("mfd", "MFD")
        };
        var elements = ProductionHeader(template, "LBL-6X4-CUSTOM-SPOUT", "CUSTOM SPOUTS");
        AddColumn(elements, template, 8, 160, 2200, ["cutPanel", "sewing", "inspector", "packing"], fields);
        AddColumn(elements, template, 16, 2500, 1500, ["length", "height", "width"], fields);
        AddColumn(elements, template, 22, 4200, 1640, ["spoutCount", "spoutDiameter", "mfd"], fields);
        return Seed(template, "LBL-6X4-CUSTOM-SPOUT", "Custom Spout 24 in 6×4", LabelSizePreset.SixByFourLandscape,
            "CUSTOM 24DIA SPOUTED BAG", new() { Fields = fields, Elements = elements });
    }

    private static LabelTemplatePresetSeed Mobile6x4()
    {
        const int template = 8;
        return Seed(template, "LBL-6X4-MOBILE", "Impresora móvil 6×4", LabelSizePreset.SixByFourLandscape,
            "6X4 FOR MOBILE PRINTER",
            new()
            {
                Elements =
                [
                    Text(template, 1, 160, 160, 900, 180, "PART NO.", 10, true),
                    Barcode(template, 2, 1050, 160, 4790, 520, "product.sku"),
                    Field(template, 3, 160, 730, 5680, 360, "product.sku", 20, true, "center"),
                    Text(template, 4, 160, 1150, 1300, 180, "DESCRIPTION", 10, true),
                    Field(template, 5, 160, 1370, 5680, 850, "product.description", 16, true, "center"),
                    Text(template, 6, 160, 2380, 500, 180, "QTY", 10, true),
                    Barcode(template, 7, 160, 2600, 2200, 520, "input.quantity"),
                    Field(template, 8, 2400, 2600, 1200, 520, "input.quantity", 18, true, "center"),
                    Text(template, 9, 160, 3220, 900, 180, "DATE MFG", 10, true),
                    Field(template, 10, 160, 3440, 2200, 320, "input.manufacturingDate", 13, true),
                    Text(template, 11, 3600, 3220, 900, 180, "REPACK", 10, true),
                    Field(template, 12, 3600, 3440, 1200, 320, "input.isRepack", 15, true, "center"),
                    Mark(template, 13, "LBL-6X4-MOBILE · v1", 4600, 3680, 1240)
                ]
            });
    }

    private static LabelTemplatePresetSeed Receiving4x45()
    {
        const int template = 9;
        var fields = new List<LabelFieldDefinition>
        {
            OptionalDate("receivingMfgDate", "Date MFG"), OptionalText("rollNumber", "Roll Number"),
            OptionalNumber("yards", "Yards"), OptionalText("purchaseOrder", "Purchase Order")
        };
        var elements = new List<LabelElementDefinition>
        {
            Text(template, 1, 160, 160, 900, 180, "PART NO.", 10, true),
            Barcode(template, 2, 160, 380, 3680, 650, "product.sku"),
            Field(template, 3, 160, 1080, 3680, 420, "product.sku", 20, true, "center")
        };
        AddReceivingField(elements, template, 4, 1600, "DATE MFG", "receivingMfgDate");
        AddReceivingField(elements, template, 6, 2250, "ROLL #", "rollNumber");
        AddReceivingField(elements, template, 8, 2900, "YDS", "yards");
        AddReceivingField(elements, template, 10, 3550, "P.O.", "purchaseOrder");
        elements.Add(Mark(template, 12, "LBL-4X45-RECEIVING · v1", 2450, 4200, 1390));
        return Seed(template, "LBL-4X45-RECEIVING", "Recepción de rollos 4×4.5", LabelSizePreset.FourByFourPointFivePortrait,
            "4x4.5 FOR RECEIVING", new() { Fields = fields, Elements = elements });
    }

    private static LabelTemplatePresetSeed Seed(int index, string code, string name, LabelSizePreset size,
        string sourceSheet, LabelDesignDocumentV1 design) =>
        new(Guid.Parse($"61000000-0000-0000-0000-{index:D12}"),
            Guid.Parse($"62000000-0000-0000-0000-{index:D12}"),
            Guid.Parse($"63000000-0000-0000-0000-{index:D12}"), code, name, size, sourceSheet, design);

    private static List<LabelElementDefinition> ProductionHeader(int template, string code, string? title = null)
    {
        var elements = new List<LabelElementDefinition>
        {
            Text(template, 1, 160, 160, 900, 160, "PART NO.", 10, true),
            Barcode(template, 2, 1050, 160, 4790, 520, "product.sku"),
            Field(template, 3, 160, 730, 5680, 360, "product.sku", 20, true, "center")
        };
        if (title is null)
        {
            elements.Add(Text(template, 4, 160, 1200, 1300, 160, "DESCRIPTION", 10, true));
            elements.Add(Field(template, 5, 160, 1400, 5680, 850, "product.description", 16, true, "center"));
            elements.Add(Mark(template, 6, $"{code} · v1", 4400, 3680, 1440));
        }
        else
        {
            elements.Add(Text(template, 4, 160, 1160, 5680, 240, title, 18, true, "center"));
            elements.Add(Text(template, 5, 160, 1450, 1300, 150, "DESCRIPTION", 10, true));
            elements.Add(Field(template, 6, 160, 1640, 5680, 610, "product.description", 15, true, "center"));
            elements.Add(Mark(template, 7, $"{code} · v1", 4200, 3680, 1640));
        }
        return elements;
    }

    private static void AddColumn(List<LabelElementDefinition> elements, int template, int firstElement,
        int x, int width, IReadOnlyList<string> bindings, IReadOnlyList<LabelFieldDefinition> fields)
    {
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            var field = fields.Single(item => item.Key == binding);
            var y = 2380 + index * 320;
            elements.Add(Text(template, firstElement + index * 2, x, y, width, 130, field.Label.ToUpperInvariant(), 8, true));
            elements.Add(Field(template, firstElement + index * 2 + 1, x, y + 135, width, 140, binding, 10, true, blankLine: true));
        }
    }

    private static void AddReceivingField(List<LabelElementDefinition> elements, int template, int firstElement,
        int y, string label, string binding)
    {
        elements.Add(Text(template, firstElement, 160, y, 1100, 180, label, 12, true));
        elements.Add(Field(template, firstElement + 1, 1320, y, 2520, 300, binding, 16, true, blankLine: true));
    }

    private static LabelFieldDefinition OptionalText(string key, string label) =>
        new() { Key = key, Label = label, Help = OptionalHelp, Type = LabelFieldType.Text };

    private static LabelFieldDefinition OptionalDate(string key, string label) =>
        new() { Key = key, Label = label, Help = OptionalHelp, Type = LabelFieldType.Date };

    private static LabelFieldDefinition OptionalNumber(string key, string label, string? defaultValue = null) =>
        new() { Key = key, Label = label, Help = OptionalHelp, Type = LabelFieldType.Number, DefaultValue = defaultValue };

    private static LabelElementDefinition Text(int template, int element, int x, int y, int width, int height,
        string text, int fontSize, bool bold = false, string align = "left") =>
        Element(template, element, LabelElementType.Text, x, y, width, height, text: text, fontSize: fontSize, bold: bold, align: align);

    private static LabelElementDefinition Field(int template, int element, int x, int y, int width, int height,
        string binding, int fontSize, bool bold = false, string align = "left", bool blankLine = false) =>
        Element(template, element, LabelElementType.Field, x, y, width, height, binding: binding, fontSize: fontSize, bold: bold, align: align, blankLine: blankLine);

    private static LabelElementDefinition Barcode(int template, int element, int x, int y, int width, int height,
        string binding) => Element(template, element, LabelElementType.Code128, x, y, width, height, binding: binding);

    private static LabelElementDefinition Mark(int template, int element, string text, int x, int y, int width) =>
        Text(template, element, x, y, width, 100, text, 5, true, "right");

    private static LabelElementDefinition Element(int template, int element, LabelElementType type,
        int x, int y, int width, int height, string? text = null, string? binding = null, int fontSize = 12,
        bool bold = false, string align = "left", bool blankLine = false) =>
        new()
        {
            Id = Guid.Parse($"7{template}000000-0000-0000-0000-{element:D12}"),
            Type = type,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Text = text,
            Binding = binding,
            FontFamily = "Arial",
            FontSize = fontSize,
            Bold = bold,
            Align = align,
            BlankLine = blankLine,
            ZIndex = 1
        };
}
