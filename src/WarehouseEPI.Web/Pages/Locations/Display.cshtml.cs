using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Locations;

public sealed record DisplayProduct(string Sku, string? Description, decimal Quantity, string Unit,
    Guid ProductId = default, bool HasActiveAssignment = false, bool HasNonZeroBalance = true, bool IsActive = true);

/// <summary>One pallet slot. State follows the rack view: negative, inactive, blocked, occupied, empty or missing.</summary>
public sealed record DisplayPosition(short PalletNumber, string? Code, string State, bool IsWip,
    IReadOnlyList<DisplayProduct> Products, Guid? LocationId = null, bool IsActive = true, bool IsBlocked = false);

public sealed record DisplayRack(string RowCode, short RackNumber, int PositionCount, int OccupiedCount,
    IReadOnlyList<DisplayPosition> Positions)
{
    public WarehouseEPI.Core.RackFormat Format { get; init; } = WarehouseEPI.Core.RackFormat.Default;
    public RackWipAssociationView? WipAssociation { get; init; }
    public string Label => $"{RowCode}-{RackNumber}";
}

public sealed record DisplayRow(string RowCode, int RackCount, int PositionCount, int OccupiedCount);

public sealed record DisplaySlide(DisplayRow Row, int Part, int Parts, IReadOnlyList<DisplayRack> Racks,
    string? LeftNeighbor, string? RightNeighbor)
{
    public string RowCode => Row.RowCode;
    public string RacksLabel { get; init; } = DisplayModel.JoinLabels(Racks.Select(rack => rack.Label).ToArray());
    public string Title { get; init; } = (Racks.Count == 1 ? "Rack " : "Racks ") + DisplayModel.JoinLabels(Racks.Select(rack => rack.Label).ToArray());
}

public sealed record DisplayMapElement(string Kind, string Label, string? RowCode, decimal X, decimal Y,
    decimal Width, decimal Height, short Rotation, short? RackNumber = null, Guid? LocationId = null);

public sealed record DisplayMapLabel(string Text, double X, double Y, bool Vertical);

public sealed record DisplayContextMap(string ViewBox, double Width, double Height,
    IReadOnlyList<DisplayMapElement> Elements, IReadOnlyList<WarehouseMapArchitecturalElementView> Architecture,
    IReadOnlyList<DisplayMapLabel> RowLabels);

public sealed record DisplayPoint(decimal X, decimal Y);

/// <summary>Context map as drawn by <c>_DisplayContextMap</c>; the exhibition highlights the first slide on the server.</summary>
public sealed record DisplayMapView(DisplayContextMap Map, DisplayPoint? Here, string Label, string? RowCode = null,
    IReadOnlyCollection<string>? CurrentRacks = null, bool Picker = false);

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed partial class DisplayModel(WarehouseDbContext db, WarehouseClock clock, WarehouseMapService maps, IStringLocalizer<CatalogTexts>? localizer = null) : PageModel
{
    private string Text(string key, params object[] args) => localizer is null ? string.Format(CultureInfo.CurrentCulture, key, args) : localizer[key, args].Value;
    public IReadOnlyList<string> AvailableRows { get; private set; } = [];
    public IReadOnlySet<string> SelectedRows { get; private set; } = new HashSet<string>();
    public IReadOnlyList<DisplaySlide> Slides { get; private set; } = [];
    public int Seconds { get; private set; } = 20;
    public int RacksPerScreen { get; private set; } = 1;
    public string Orientation { get; private set; } = "auto";
    public int EffectiveRacksPerScreen => Orientation == "portrait" ? 1 : RacksPerScreen;
    public string Order { get; private set; } = "asc";
    public bool Play { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset GeneratedAt { get; private set; }
    public string ConfigurationUrl { get; private set; } = "/Locations/Display";
    public DisplayContextMap? ContextMap { get; private set; }
    public DisplayPoint? Here { get; private set; }

    public async Task OnGetAsync(string[]? rows, int? seconds, int? racks = null, string? here = null,
        bool play = false, bool refresh = false, string? order = null, string? orientation = null,
        CancellationToken cancellationToken = default)
    {
        Seconds = seconds is >= 5 and <= 120 ? seconds.Value : 20;
        RacksPerScreen = racks is >= 1 and <= 3 ? racks.Value : 1;
        Orientation = orientation?.ToLowerInvariant() switch
        {
            "landscape" => "landscape",
            "portrait" => "portrait",
            _ => "auto"
        };
        Order = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
        AvailableRows = await db.Locations.AsNoTracking()
            .Where(location => location.Kind == LocationKind.Rack && location.IsPhysicallyPresent &&
                location.RowCode != null)
            .Select(location => location.RowCode!).Distinct().OrderBy(row => row)
            .ToListAsync(cancellationToken);

        var requested = new HashSet<string>((rows ?? []).Select(row => row.Trim().ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);
        SelectedRows = new HashSet<string>(AvailableRows.Where(row =>
            !play && rows is null || requested.Contains(row)), StringComparer.OrdinalIgnoreCase);
        Play = play && SelectedRows.Count > 0;
        if (play && !Play) Error = Text("Selecciona al menos una fila para iniciar la exhibición.");

        // A refresh only swaps slides; the static context map stays as the page first drew it.
        if (!(Play && refresh) && AvailableRows.Count > 0)
        {
            var context = await maps.GetDisplayContextAsync(cancellationToken);
            ContextMap = BuildContextMap(context);
            Here = ParseHere(here, context);
        }
        ConfigurationUrl = "/Locations/Display?seconds=" + Seconds + "&racks=" + RacksPerScreen + "&order=" + Order +
            "&orientation=" + Orientation + string.Concat(
            AvailableRows.Where(SelectedRows.Contains).Select(row => "&rows=" + Uri.EscapeDataString(row))) +
            (Here is null ? "" : "&here=" + Uri.EscapeDataString(FormatPoint(Here)));
        if (!Play) return;

        var selectedRows = SelectedRows.ToArray();
        var displayRacks = await maps.GetDisplayRacksAsync(selectedRows, cancellationToken);
        var locations = await db.Locations.AsNoTracking()
            .Where(location => location.Kind == LocationKind.Rack && location.IsPhysicallyPresent &&
                location.RowCode != null && location.RackNumber != null && selectedRows.Contains(location.RowCode))
            .Select(location => new
            {
                location.Id,
                location.Code,
                location.RowCode,
                location.RackNumber,
                location.PalletNumber,
                location.IsActive,
                location.IsBlocked,
                location.OperationalRole
            })
            .ToListAsync(cancellationToken);
        var locationIds = locations.Select(location => location.Id).ToArray();
        var productsByLocation = await LoadDisplayProductsAsync(locationIds, false, cancellationToken);
        var formats = await LocationRackFormats.LoadAsync(db, cancellationToken);
        var associations = await LocationRackWipAssociations.LoadAsync(db, cancellationToken);

        var slides = new List<DisplaySlide>();
        var orderedRows = AvailableRows.Where(SelectedRows.Contains);
        foreach (var row in Order == "desc" ? orderedRows.Reverse() : orderedRows)
        {
            var rowLocations = locations.Where(location => location.RowCode == row).ToArray();
            var rowRacks = rowLocations.GroupBy(location => location.RackNumber!.Value).ToDictionary(group => group.Key,
                group =>
                {
                    var byPallet = group.Where(location => location.PalletNumber != null)
                        .GroupBy(location => location.PalletNumber!.Value)
                        .ToDictionary(pallets => pallets.Key, pallets => pallets.First());
                    var format = formats.GetValueOrDefault((row, group.Key)) ?? WarehouseEPI.Core.RackFormat.Default;
                    var positions = format.PalletOrder.Select(pallet =>
                    {
                        if (!byPallet.TryGetValue(pallet, out var location))
                            return new DisplayPosition(pallet, null, "missing", false, []);
                        var products = productsByLocation.GetValueOrDefault(location.Id) ?? [];
                        var state = products.Any(product => product.Quantity < 0) ? "negative"
                            : !location.IsActive ? "inactive"
                            : location.IsBlocked ? "blocked"
                            : products.Count > 0 ? "occupied" : "empty";
                        return new DisplayPosition(pallet, location.Code, state,
                            location.OperationalRole == LocationOperationalRole.Wip, products, location.Id,
                            location.IsActive, location.IsBlocked);
                    }).ToArray();
                    return new DisplayRack(row, group.Key, group.Count(x => x.OperationalRole != LocationOperationalRole.Wip),
                        group.Count(location => productsByLocation.ContainsKey(location.Id)), positions)
                    { Format = format, WipAssociation = associations.GetValueOrDefault((row, group.Key)) };
                });
            var displayRow = new DisplayRow(row, rowRacks.Count, rowLocations.Count(x => x.OperationalRole != LocationOperationalRole.Wip),
                rowLocations.Count(location => productsByLocation.ContainsKey(location.Id)));
            var seen = new HashSet<short>();
            var physicalOrder = PhysicalOrder(displayRacks.Where(rack => rack.RowCode == row).ToArray())
                .Concat(rowRacks.Keys.Order())
                .Where(number => rowRacks.ContainsKey(number) && seen.Add(number)).ToArray();
            var traversal = Order == "desc" ? physicalOrder.Reverse() : physicalOrder;
            var groups = traversal.Chunk(EffectiveRacksPerScreen).ToArray();
            for (var part = 0; part < groups.Length; part++)
            {
                var indices = groups[part].Select(number => Array.IndexOf(physicalOrder, number)).ToArray();
                var first = indices.Min();
                var last = indices.Max();
                var groupRacks = groups[part].Select(number => rowRacks[number]).ToArray();
                var labels = groupRacks.Select(rack => rack.Label).ToArray();
                var racksLabel = labels.Length == 1 ? labels[0] : Text("{0} y {1}", string.Join(", ", labels[..^1]), labels[^1]);
                slides.Add(new DisplaySlide(displayRow, part + 1, groups.Length,
                    groupRacks,
                    first > 0 ? $"{row}-{physicalOrder[first - 1]}" : null,
                    last < physicalOrder.Length - 1 ? $"{row}-{physicalOrder[last + 1]}" : null)
                { RacksLabel = racksLabel, Title = Text(groupRacks.Length == 1 ? "Rack {0}" : "Racks {0}", racksLabel) });
            }
        }
        Slides = slides;
        GeneratedAt = await clock.ConvertAsync(DateTimeOffset.UtcNow, cancellationToken);
    }

    public static string JoinLabels(IReadOnlyList<string> labels) => labels.Count switch
    {
        0 => "",
        1 => labels[0],
        _ => string.Join(", ", labels.Take(labels.Count - 1)) + " y " + labels[^1]
    };

    public static string Format(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatPoint(DisplayPoint point) => Format(point.X) + "," + Format(point.Y);

    /// <summary>Racks as they sit on the published map: along the row's longest axis, left to right or top to bottom.</summary>
    private static IEnumerable<short> PhysicalOrder(IReadOnlyList<WarehouseMapDisplayRack> racks)
    {
        var placed = racks.Where(rack => rack.IsPlaced)
            .Select(rack => (rack.RackNumber, Center: Center(rack.X, rack.Y, rack.Width, rack.Height, rack.Rotation)))
            .ToArray();
        var horizontal = placed.Length < 2 ||
            placed.Max(item => item.Center.X) - placed.Min(item => item.Center.X) >=
            placed.Max(item => item.Center.Y) - placed.Min(item => item.Center.Y);
        var ordered = horizontal
            ? placed.OrderBy(item => item.Center.X).ThenBy(item => item.Center.Y)
            : placed.OrderBy(item => item.Center.Y).ThenBy(item => item.Center.X);
        return ordered.ThenBy(item => item.RackNumber).Select(item => item.RackNumber)
            .Concat(racks.Where(rack => !rack.IsPlaced).OrderBy(rack => rack.RackNumber).Select(rack => rack.RackNumber));
    }

    private static DisplayContextMap? BuildContextMap(WarehouseMapDisplayContext context)
    {
        if (context.Elements.Count == 0) return null;
        var core = Box.Union(context.Elements.Select(item => Box.Of(item.X, item.Y, item.Width, item.Height, item.Rotation)));
        var reach = core.Inflate(100);
        var architecture = context.Architecture.Where(item => ArchitectureBox(item).Intersects(reach)).ToArray();
        var frame = Box.Union(architecture.Select(ArchitectureBox).Append(core)).Inflate(16)
            .Clamp((double)context.CanvasWidth, (double)context.CanvasHeight);
        var rowLabels = context.Elements.Where(item => item.Kind == "Rack" && item.RowCode is not null)
            .GroupBy(item => item.RowCode!)
            .Select(group =>
            {
                var boxes = group.Select(item => Box.Of(item.X, item.Y, item.Width, item.Height, item.Rotation)).ToArray();
                var row = Box.Union(boxes);
                var vertical = row.Height > row.Width;
                return vertical
                    ? new DisplayMapLabel(group.Key, (row.MinX + row.MaxX) / 2, row.MinY - 14, true)
                    : new DisplayMapLabel(group.Key, row.MinX - 14, (row.MinY + row.MaxY) / 2, false);
            }).OrderBy(label => label.Text, StringComparer.Ordinal).ToArray();
        var elements = context.Elements.Select(item => new DisplayMapElement(item.Kind, item.Label, item.RowCode,
            item.X, item.Y, item.Width, item.Height, item.Rotation, item.RackNumber, item.LocationId)).ToArray();
        return new(string.Join(" ", new[] { frame.MinX, frame.MinY, frame.Width, frame.Height }.Select(Format)),
            frame.Width, frame.Height, elements, architecture, rowLabels);
    }

    private static DisplayPoint? ParseHere(string? value, WarehouseMapDisplayContext context)
    {
        var parts = (value ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !decimal.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var x) ||
            !decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var y) ||
            x > context.CanvasWidth || y > context.CanvasHeight)
            return null;
        return new(decimal.Round(x), decimal.Round(y));
    }

    private static (double X, double Y) Center(decimal x, decimal y, decimal width, decimal height, short rotation)
    {
        var angle = rotation * Math.PI / 180;
        var halfWidth = (double)width / 2;
        var halfHeight = (double)height / 2;
        return ((double)x + halfWidth * Math.Cos(angle) - halfHeight * Math.Sin(angle),
            (double)y + halfWidth * Math.Sin(angle) + halfHeight * Math.Cos(angle));
    }

    private static Box ArchitectureBox(WarehouseMapArchitecturalElementView item)
    {
        IEnumerable<(double X, double Y)> points = item.Kind switch
        {
            "Rectangle" => [(0, 0), ((double)item.Width, 0), (0, (double)item.Height), ((double)item.Width, (double)item.Height)],
            "Polyline" => item.Points.Select(point => ((double)point.X, (double)point.Y)),
            _ => [(0, 0), ((item.Label?.Length ?? 4) * 10d, 22)]
        };
        return Box.Of(points, (double)item.X, (double)item.Y, item.Rotation);
    }

    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        public static Box Of(decimal x, decimal y, decimal width, decimal height, short rotation) =>
            Of([(0, 0), ((double)width, 0), (0, (double)height), ((double)width, (double)height)], (double)x, (double)y, rotation);

        public static Box Of(IEnumerable<(double X, double Y)> points, double x, double y, short rotation)
        {
            var angle = rotation * Math.PI / 180;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var placed = points.Select(point => (X: x + point.X * cos - point.Y * sin, Y: y + point.X * sin + point.Y * cos))
                .ToArray();
            return placed.Length == 0 ? new(x, y, x, y) : new(placed.Min(point => point.X), placed.Min(point => point.Y),
                placed.Max(point => point.X), placed.Max(point => point.Y));
        }

        public static Box Union(IEnumerable<Box> boxes) => boxes.Aggregate((left, right) => new(
            Math.Min(left.MinX, right.MinX), Math.Min(left.MinY, right.MinY),
            Math.Max(left.MaxX, right.MaxX), Math.Max(left.MaxY, right.MaxY)));

        public Box Inflate(double amount) => new(MinX - amount, MinY - amount, MaxX + amount, MaxY + amount);

        public Box Clamp(double width, double height) => new(Math.Max(0, MinX), Math.Max(0, MinY),
            Math.Min(width, MaxX), Math.Min(height, MaxY));

        public bool Intersects(Box other) =>
            MinX <= other.MaxX && MaxX >= other.MinX && MinY <= other.MaxY && MaxY >= other.MinY;
    }
}
