using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Labels;

public enum LabelFieldType { Text, Number, Date, Boolean, Select }
public enum LabelElementType { Text, Field, Code128, Image, Line, Rectangle }

public sealed record LabelSizeDefinition(LabelSizePreset Preset, string Code, string Name, int WidthMils, int HeightMils)
{
    public decimal WidthInches => WidthMils / 1000m;
    public decimal HeightInches => HeightMils / 1000m;
}

public static class LabelSizeRegistry
{
    public static readonly IReadOnlyList<LabelSizeDefinition> All =
    [
        new(LabelSizePreset.SixByFourLandscape, "6X4_L", "6×4 horizontal", 6000, 4000),
        new(LabelSizePreset.FourBySixPortrait, "4X6_P", "4×6 vertical", 4000, 6000),
        new(LabelSizePreset.ThreeByOneLandscape, "3X1_L", "3×1 horizontal", 3000, 1000),
        new(LabelSizePreset.FourByFourPointFivePortrait, "4X45_P", "4×4.5 vertical", 4000, 4500),
        new(LabelSizePreset.ElevenByEightPointFiveLandscape, "11X85_L", "Carta horizontal 11×8.5", 11000, 8500)
    ];

    public static LabelSizeDefinition Get(LabelSizePreset preset) => All.Single(item => item.Preset == preset);
}

public sealed class LabelDesignDocumentV1
{
    public int SchemaVersion { get; set; } = 1;
    public List<LabelFieldDefinition> Fields { get; set; } = [];
    public List<LabelElementDefinition> Elements { get; set; } = [];
}

public sealed class LabelFieldDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Help { get; set; }
    public LabelFieldType Type { get; set; }
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public List<string> Options { get; set; } = [];
}

public sealed class LabelElementDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LabelElementType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1000;
    public int Height { get; set; } = 300;
    public int Rotation { get; set; }
    public int ZIndex { get; set; }
    public string? Text { get; set; }
    public string? Binding { get; set; }
    public Guid? AssetId { get; set; }
    public string? BuiltInAssetKey { get; set; }
    public string FontFamily { get; set; } = "Arial";
    public int FontSize { get; set; } = 18;
    public bool Bold { get; set; }
    public string Color { get; set; } = "#000000";
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public int BorderWidth { get; set; } = 1;
    public string Align { get; set; } = "left";
    public bool BlankLine { get; set; }
}

public sealed record LabelDesignValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public static partial class LabelDesignSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly HashSet<string> ProductBindings = new(StringComparer.Ordinal) { "product.sku", "product.description", "product.unit", "product.externalReference" };
    private static readonly HashSet<string> ProductInputBindings = new(StringComparer.Ordinal) { "input.quantity", "input.manufacturingDate", "input.isRepack" };
    private static readonly HashSet<string> PalletBindings = new(StringComparer.Ordinal) { "plate.identifier", "entry.reference", "entry.occurredDate", "entry.responsible", "entry.destination", "entry.quantity", "entry.unit" };
    public static readonly IReadOnlyList<(string Group, string Value, string Label)> ProductBindingOptions = [ ("Catálogo", "product.sku", "SKU"), ("Catálogo", "product.description", "Descripción"), ("Catálogo", "product.unit", "Unidad"), ("Catálogo", "product.externalReference", "Referencia externa"), ("Operación", "input.quantity", "Cantidad"), ("Operación", "input.manufacturingDate", "Fecha MFG"), ("Operación", "input.isRepack", "Repack") ];
    public static readonly IReadOnlyList<(string Group, string Value, string Label)> PalletBindingOptions = [ ("Catálogo", "product.sku", "SKU"), ("Catálogo", "product.description", "Descripción"), ("Catálogo", "product.unit", "Unidad"), ("Catálogo", "product.externalReference", "Referencia externa"), ("Placa", "plate.identifier", "Folio de placa"), ("Entrada", "entry.occurredDate", "Fecha de entrada"), ("Entrada", "entry.responsible", "Responsable"), ("Entrada", "entry.destination", "Destino"), ("Entrada", "entry.reference", "Referencia"), ("Entrada", "entry.quantity", "Cantidad"), ("Entrada", "entry.unit", "Unidad de entrada") ];
    private static readonly HashSet<string> Fonts = new(StringComparer.Ordinal) { "Arial", "Helvetica", "Times New Roman", "Courier New" };
    private static readonly HashSet<string> Alignments = new(StringComparer.Ordinal) { "left", "center", "right" };

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{1,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldKeyRegex();
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorRegex();

    public static string Serialize(LabelDesignDocumentV1 document) => JsonSerializer.Serialize(document, Options);

    public static LabelDesignDocumentV1? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<LabelDesignDocumentV1>(json, Options); }
        catch (JsonException) { return null; }
    }

    public static LabelDesignValidation Validate(LabelDesignDocumentV1? document, LabelSizePreset preset, LabelTemplateKind kind = LabelTemplateKind.ProductLabel)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (document is null) return new(["El documento JSON no es válido."], []);
        document.Fields ??= [];
        document.Elements ??= [];
        if (document.SchemaVersion != 1) errors.Add("La versión del esquema debe ser 1.");
        if (document.Fields.Count > 40) errors.Add("La plantilla admite como máximo 40 campos personalizados.");
        if (document.Elements.Count is < 1 or > 150) errors.Add("La plantilla debe contener entre 1 y 150 elementos.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in document.Fields)
        {
            field.Key ??= string.Empty;
            field.Label ??= string.Empty;
            field.Options ??= [];
            if (!FieldKeyRegex().IsMatch(field.Key) || ProductBindings.Contains(field.Key) || ProductInputBindings.Contains(field.Key) || PalletBindings.Contains(field.Key)) errors.Add($"La clave de campo '{field.Key}' no es válida.");
            else if (!keys.Add(field.Key)) errors.Add($"La clave de campo '{field.Key}' está repetida.");
            if (string.IsNullOrWhiteSpace(field.Label) || field.Label.Trim().Length > 80) errors.Add($"El campo '{field.Key}' requiere una etiqueta de hasta 80 caracteres.");
            if (field.Help?.Length > 200) errors.Add($"La ayuda de '{field.Key}' excede 200 caracteres.");
            if (field.DefaultValue?.Length > 200) errors.Add($"El valor inicial de '{field.Key}' excede 200 caracteres.");
            if (UnsafeConfiguredText(field.Label) || UnsafeConfiguredText(field.Help) || UnsafeConfiguredText(field.DefaultValue) || field.Options.Any(UnsafeConfiguredText))
                errors.Add($"El campo '{field.Key}' contiene markup, URL, ruta o texto no permitido.");
            if (field.Type == LabelFieldType.Select)
            {
                if (field.Options.Count is < 1 or > 50 || field.Options.Any(option => string.IsNullOrWhiteSpace(option) || option.Length > 80) || field.Options.Distinct(StringComparer.Ordinal).Count() != field.Options.Count)
                    errors.Add($"La lista '{field.Key}' debe contener entre 1 y 50 opciones únicas.");
            }
            else if (field.Options.Count != 0) errors.Add($"Solo los campos de lista pueden contener opciones ({field.Key}).");
        }

        var size = LabelSizeRegistry.Get(preset);
        var ids = new HashSet<Guid>();
        foreach (var element in document.Elements)
        {
            element.FontFamily ??= string.Empty;
            element.Color ??= string.Empty;
            element.BackgroundColor ??= string.Empty;
            element.Align ??= string.Empty;
            if (element.Id == Guid.Empty || !ids.Add(element.Id)) errors.Add("Cada elemento debe tener un identificador único.");
            if (element.Width < 10 || element.Height < 10 || element.X < 0 || element.Y < 0 || element.X + element.Width > size.WidthMils || element.Y + element.Height > size.HeightMils)
                errors.Add($"El elemento {element.Id} está fuera del lienzo.");
            if (element.Rotation is < -180 or > 180) errors.Add($"La rotación del elemento {element.Id} no es válida.");
            if (element.ZIndex is < 0 or > 1000) errors.Add($"La capa del elemento {element.Id} no es válida.");
            if (!Fonts.Contains(element.FontFamily) || element.FontSize is < 5 or > 144) errors.Add($"La tipografía del elemento {element.Id} no está permitida.");
            if (!ColorRegex().IsMatch(element.Color) || !ColorRegex().IsMatch(element.BackgroundColor) || element.BorderWidth is < 0 or > 20 || !Alignments.Contains(element.Align))
                errors.Add($"El estilo del elemento {element.Id} no es válido.");
            if (element.Text?.Length > 500 || element.Text?.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t') == true)
                errors.Add($"El texto del elemento {element.Id} no es válido.");
            if (UnsafeConfiguredText(element.Text)) errors.Add($"El elemento {element.Id} contiene markup, URL o ruta no permitida.");
            if (element.Type is LabelElementType.Field or LabelElementType.Code128)
            {
                if (string.IsNullOrWhiteSpace(element.Binding) || !(BindingsFor(kind).Contains(element.Binding) || keys.Contains(element.Binding)))
                    errors.Add($"El elemento {element.Id} apunta a un campo desconocido.");
            }
            else if (element.Binding is not null) errors.Add($"El elemento {element.Id} no admite binding.");
            if (element.Type == LabelElementType.Image && element.AssetId is null && element.BuiltInAssetKey is null) errors.Add($"La imagen {element.Id} no tiene un recurso asociado.");
            if (element.Type == LabelElementType.Image && element.AssetId is not null && element.BuiltInAssetKey is not null) errors.Add($"La imagen {element.Id} debe usar un solo recurso.");
            if (element.Type == LabelElementType.Image && element.BuiltInAssetKey is not null && element.BuiltInAssetKey != "extra-packaging-logo") errors.Add($"La imagen {element.Id} usa un recurso integrado desconocido.");
            if (element.Type != LabelElementType.Image && (element.AssetId is not null || element.BuiltInAssetKey is not null)) errors.Add($"El elemento {element.Id} no admite imágenes.");
            if (element.Type == LabelElementType.Text && string.IsNullOrWhiteSpace(element.Text)) errors.Add($"El texto fijo {element.Id} está vacío.");
            if (element.X < 140 || element.Y < 140 || element.X + element.Width > size.WidthMils - 140 || element.Y + element.Height > size.HeightMils - 140)
                warnings.Add($"El elemento {element.Id} invade el margen seguro de 0.14 pulgadas.");
            if (element.Type == LabelElementType.Code128 && element.Width < 1200) warnings.Add($"El Code 128 {element.Id} puede resultar demasiado denso.");
        }
        return new(errors.Distinct().ToArray(), warnings.Distinct().ToArray());
    }

    private static bool UnsafeConfiguredText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return value.Contains('<') || value.Contains('>') || value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("://", StringComparison.Ordinal) || value.Contains('\\') ||
            value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t');
    }

    public static IReadOnlyList<(string Group, string Value, string Label)> BindingOptions(LabelTemplateKind kind) => kind == LabelTemplateKind.PalletLicensePlate ? PalletBindingOptions : ProductBindingOptions;
    public static bool IsSystemBinding(LabelTemplateKind kind, string? binding) => binding is not null && BindingsFor(kind).Contains(binding);
    private static HashSet<string> BindingsFor(LabelTemplateKind kind) => kind == LabelTemplateKind.PalletLicensePlate ? new(ProductBindings.Concat(PalletBindings), StringComparer.Ordinal) : new(ProductBindings.Concat(ProductInputBindings), StringComparer.Ordinal);

    public static LabelDesignDocumentV1 Seed6x4() => new()
    {
        Fields = [],
        Elements =
        [
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000001"), Type = LabelElementType.Text, X = 160, Y = 150, Width = 900, Height = 220, Text = "PART NO.", FontSize = 12, Bold = true, ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000002"), Type = LabelElementType.Code128, X = 1050, Y = 150, Width = 4750, Height = 700, Binding = "product.sku", ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000003"), Type = LabelElementType.Field, X = 160, Y = 830, Width = 5640, Height = 420, Binding = "product.sku", FontSize = 24, Bold = true, Align = "center", ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000004"), Type = LabelElementType.Text, X = 160, Y = 1320, Width = 1300, Height = 220, Text = "DESCRIPTION", FontSize = 12, Bold = true, ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000005"), Type = LabelElementType.Field, X = 160, Y = 1540, Width = 5640, Height = 900, Binding = "product.description", FontSize = 21, Bold = true, Align = "center", ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000006"), Type = LabelElementType.Text, X = 160, Y = 2550, Width = 500, Height = 200, Text = "QTY", FontSize = 11, Bold = true, ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000007"), Type = LabelElementType.Code128, X = 160, Y = 2750, Width = 2050, Height = 500, Binding = "input.quantity", ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000008"), Type = LabelElementType.Field, X = 2250, Y = 2760, Width = 850, Height = 450, Binding = "input.quantity", FontSize = 20, Bold = true, Align = "center", ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000009"), Type = LabelElementType.Text, X = 3250, Y = 2550, Width = 1000, Height = 200, Text = "DATE MFG", FontSize = 11, Bold = true, ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000010"), Type = LabelElementType.Field, X = 3250, Y = 2800, Width = 1250, Height = 400, Binding = "input.manufacturingDate", FontSize = 14, Bold = true, ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000011"), Type = LabelElementType.Text, X = 4700, Y = 2550, Width = 900, Height = 200, Text = "REPACK", FontSize = 11, Bold = true, ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000012"), Type = LabelElementType.Field, X = 4700, Y = 2800, Width = 900, Height = 400, Binding = "input.isRepack", FontSize = 18, Bold = true, Align = "center", ZIndex = 1 },
            new() { Id = Guid.Parse("10000000-0000-0000-0000-000000000013"), Type = LabelElementType.Text, X = 4700, Y = 3650, Width = 1100, Height = 150, Text = "LBL-6X4-ZEBRA · v1", FontSize = 7, Bold = true, Align = "right", ZIndex = 1 }
        ]
    };
}
