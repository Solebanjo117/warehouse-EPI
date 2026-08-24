using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Locations;

public sealed record WarehouseMapPoint(decimal X, decimal Y);

public sealed record WarehouseMapArchitectureItem(
    Guid Id,
    string LayerCode,
    string Kind,
    string? Label,
    decimal X,
    decimal Y,
    decimal Width,
    decimal Height,
    short Rotation,
    decimal CornerRadius,
    IReadOnlyList<WarehouseMapPoint> Points,
    string StrokeToken,
    string FillToken,
    decimal StrokeWidth,
    bool IsDashed,
    int ZIndex,
    bool IsLocked);

public sealed record WarehouseMapLayerState(string Code, bool IsLocked);

public sealed record WarehouseMapLayerView(
    Guid Id,
    string Code,
    string Name,
    short SortOrder,
    bool IsLocked,
    int ElementCount);

public sealed record WarehouseMapArchitecturalElementView(
    Guid Id,
    string LayerCode,
    string Kind,
    string? Label,
    decimal X,
    decimal Y,
    decimal Width,
    decimal Height,
    short Rotation,
    decimal CornerRadius,
    IReadOnlyList<WarehouseMapPoint> Points,
    string StrokeToken,
    string FillToken,
    decimal StrokeWidth,
    bool IsDashed,
    int ZIndex,
    bool IsLocked);

internal sealed record WarehouseMapStoredGeometry(
    decimal X,
    decimal Y,
    decimal Width,
    decimal Height,
    short Rotation,
    decimal CornerRadius,
    IReadOnlyList<WarehouseMapPoint> Points);

internal static class WarehouseMapArchitectureCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static readonly IReadOnlySet<string> StyleTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "NONE", "SECONDARY", "PRIMARY", "INFO", "WARNING", "SUCCESS"
    };

    public static List<WarehouseMapLayer> CreateLayers() =>
    [
        Layer(WarehouseMapLayerCode.Structure, "Estructura", 10, true),
        Layer(WarehouseMapLayerCode.Aisles, "Pasillos", 20, true),
        Layer(WarehouseMapLayerCode.Zones, "Zonas", 30, true),
        Layer(WarehouseMapLayerCode.Text, "Textos", 40, true),
        Layer(WarehouseMapLayerCode.Dimensions, "Medidas", 50, true),
        Layer(WarehouseMapLayerCode.Operations, "Ubicaciones operativas", 60, false)
    ];

    public static List<WarehouseMapArchitecturalElement> CreateElements(IReadOnlyList<WarehouseMapLayer> layers)
    {
        var byCode = layers.ToDictionary(item => item.Code);
        return
        [
            Rectangle("BUILDING-OUTLINE", byCode[WarehouseMapLayerCode.Structure], 35, 35, 1530, 820, 12, 10),

            Polyline("AISLE-SOUTH", byCode[WarehouseMapLayerCode.Aisles], 55, 740, 1490, 1, [new(0, 0), new(1490, 0)], 20),
            Polyline("AISLE-NORTH-WEST", byCode[WarehouseMapLayerCode.Aisles], 260, 35, 1, 120, [new(0, 0), new(0, 120)], 21),
            Polyline("AISLE-WEST", byCode[WarehouseMapLayerCode.Aisles], 55, 250, 205, 1, [new(0, 0), new(205, 0)], 22),
            Polyline("AISLE-NORTH-EAST", byCode[WarehouseMapLayerCode.Aisles], 1250, 35, 1, 115, [new(0, 0), new(0, 115)], 23),
            Polyline("AISLE-SHIPPING", byCode[WarehouseMapLayerCode.Aisles], 1495, 150, 1, 165, [new(0, 0), new(0, 165)], 24),
            Polyline("AISLE-PACKING", byCode[WarehouseMapLayerCode.Aisles], 1260, 805, 285, 1, [new(0, 0), new(285, 0)], 25),

            Rectangle("ZONE-KPA", byCode[WarehouseMapLayerCode.Zones], 55, 55, 205, 95, 0, 30),
            Rectangle("ZONE-SEWING", byCode[WarehouseMapLayerCode.Zones], 55, 250, 205, 480, 0, 31),
            Rectangle("ZONE-PACKING", byCode[WarehouseMapLayerCode.Zones], 1260, 805, 285, 50, 0, 32),
            Rectangle("ZONE-SHIPPING", byCode[WarehouseMapLayerCode.Zones], 1370, 320, 125, 100, 0, 33),

            Text("TEXT-KPA", byCode[WarehouseMapLayerCode.Text], "KPA / Breakroom", 85, 67, 210, 24, 40),
            Text("TEXT-SEWING", byCode[WarehouseMapLayerCode.Text], "Sewing", 90, 432, 100, 24, 41),
            Text("TEXT-PACKING", byCode[WarehouseMapLayerCode.Text], "Packing / Producción", 1275, 820, 230, 24, 42),
            Text("TEXT-SHIPPING", byCode[WarehouseMapLayerCode.Text], "Shipping", 1390, 357, 100, 24, 43),
            Text("TEXT-CARTON", byCode[WarehouseMapLayerCode.Text], "Carton", 1370, 772, 100, 24, 44),
            Text("TEXT-FC-ROLLS", byCode[WarehouseMapLayerCode.Text], "FC Rolls", 760, 30, 100, 24, 45)
        ];
    }

    public static WarehouseMapStoredGeometry ReadGeometry(WarehouseMapArchitecturalElement item) =>
        JsonSerializer.Deserialize<WarehouseMapStoredGeometry>(item.GeometryJson, JsonOptions)
        ?? throw new InvalidOperationException($"La geometría arquitectónica {item.Id} no es válida.");

    public static string WriteGeometry(WarehouseMapArchitectureItem item) => JsonSerializer.Serialize(
        new WarehouseMapStoredGeometry(item.X, item.Y, item.Width, item.Height, item.Rotation, item.CornerRadius, item.Points), JsonOptions);

    public static WarehouseMapArchitectureItem ToItem(WarehouseMapArchitecturalElement item, string layerCode)
    {
        var geometry = ReadGeometry(item);
        return new(item.Id, layerCode, item.Kind.ToString(), item.Label, geometry.X, geometry.Y, geometry.Width,
            geometry.Height, geometry.Rotation, geometry.CornerRadius, geometry.Points, item.StrokeToken,
            item.FillToken, item.StrokeWidth, item.IsDashed, item.ZIndex, item.IsLocked);
    }

    public static WarehouseMapArchitecturalElementView ToView(WarehouseMapArchitecturalElement item, string layerCode)
    {
        var geometry = ReadGeometry(item);
        return new(item.Id, layerCode, item.Kind.ToString(), item.Label, geometry.X, geometry.Y, geometry.Width,
            geometry.Height, geometry.Rotation, geometry.CornerRadius, geometry.Points, item.StrokeToken,
            item.FillToken, item.StrokeWidth, item.IsDashed, item.ZIndex, item.IsLocked);
    }

    public static string Code(WarehouseMapLayerCode code) => code switch
    {
        WarehouseMapLayerCode.Structure => "STRUCTURE",
        WarehouseMapLayerCode.Aisles => "AISLES",
        WarehouseMapLayerCode.Zones => "ZONES",
        WarehouseMapLayerCode.Text => "TEXT",
        WarehouseMapLayerCode.Dimensions => "DIMENSIONS",
        _ => "OPERATIONS"
    };

    public static WarehouseMapArchitecturalElement CreateElement(
        WarehouseMapArchitectureItem item, WarehouseMapLayer layer, int zIndex) => new()
        {
            Id = item.Id,
            LayerId = layer.Id,
            Kind = Enum.Parse<WarehouseMapArchitecturalElementKind>(item.Kind, true),
            Label = string.IsNullOrWhiteSpace(item.Label) ? null : item.Label.Trim(),
            GeometryJson = WriteGeometry(item),
            StrokeToken = item.StrokeToken,
            FillToken = item.FillToken,
            StrokeWidth = item.StrokeWidth,
            IsDashed = item.IsDashed,
            ZIndex = zIndex,
            IsLocked = false
        };

    private static WarehouseMapLayer Layer(WarehouseMapLayerCode code, string name, short order, bool locked) => new()
    {
        Id = StableId($"LAYER|{Code(code)}"),
        Code = code,
        Name = name,
        SortOrder = order,
        IsLocked = locked
    };

    private static WarehouseMapArchitecturalElement Rectangle(string key, WarehouseMapLayer layer, decimal x,
        decimal y, decimal width, decimal height, decimal radius, int zIndex) => Element(key, layer,
            WarehouseMapArchitecturalElementKind.Rectangle, null,
            new(x, y, width, height, 0, radius, []), "SECONDARY", "NONE", 2, false, zIndex);

    private static WarehouseMapArchitecturalElement Polyline(string key, WarehouseMapLayer layer, decimal x,
        decimal y, decimal width, decimal height, IReadOnlyList<WarehouseMapPoint> points, int zIndex) =>
        Element(key, layer, WarehouseMapArchitecturalElementKind.Polyline, null,
            new(x, y, width, height, 0, 0, points), "SECONDARY", "NONE", 2, true, zIndex);

    private static WarehouseMapArchitecturalElement Text(string key, WarehouseMapLayer layer, string label,
        decimal x, decimal y, decimal width, decimal height, int zIndex) => Element(key, layer,
            WarehouseMapArchitecturalElementKind.Text, label, new(x, y, width, height, 0, 0, []),
            "NONE", "SECONDARY", 0, false, zIndex);

    private static WarehouseMapArchitecturalElement Element(string key, WarehouseMapLayer layer,
        WarehouseMapArchitecturalElementKind kind, string? label, WarehouseMapStoredGeometry geometry,
        string stroke, string fill, decimal strokeWidth, bool dashed, int zIndex) => new()
        {
            Id = StableId($"ARCH|{key}"),
            LayerId = layer.Id,
            Layer = layer,
            Kind = kind,
            Label = label,
            GeometryJson = JsonSerializer.Serialize(geometry, JsonOptions),
            StrokeToken = stroke,
            FillToken = fill,
            StrokeWidth = strokeWidth,
            IsDashed = dashed,
            ZIndex = zIndex
        };

    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
