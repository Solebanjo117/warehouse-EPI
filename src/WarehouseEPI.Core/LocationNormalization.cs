using System.Text.RegularExpressions;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Core;

public static partial class LocationNormalization
{
    public const int MaxCodeLength = 40;

    public static string NormalizeCode(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    public static string NormalizeRowCode(string? value) => NormalizeCode(value);
    public static string BuildRackCode(string? rowCode, short rackNumber, short palletNumber) =>
        $"{NormalizeRowCode(rowCode)}-{rackNumber}-{palletNumber}";
    public static bool IsValidRowCode(string value) => RowCodePattern().IsMatch(value);
    public static bool IsValidAreaCode(string value) =>
        value.Length is > 0 and <= MaxCodeLength && AreaCodePattern().IsMatch(value);
    public static bool IsValidRack(short rackNumber, short palletNumber) =>
        rackNumber > 0 && palletNumber is >= 1 and <= 9;
    public static string LevelName(short palletNumber) => palletNumber switch
    {
        >= 1 and <= 3 => "Inferior",
        >= 4 and <= 6 => "Medio",
        >= 7 and <= 9 => "Superior",
        _ => throw new ArgumentOutOfRangeException(nameof(palletNumber))
    };
    public static string NormalizeForLookup(string? value) => NormalizeCode(value);
    public static bool IsStructurallyValid(Location location) => location.Kind switch
    {
        LocationKind.Rack => location.RowCode is not null && IsValidRowCode(location.RowCode) &&
            location.RackNumber is not null && location.PalletNumber is not null &&
            IsValidRack(location.RackNumber.Value, location.PalletNumber.Value) &&
            location.Code == BuildRackCode(location.RowCode, location.RackNumber.Value, location.PalletNumber.Value),
        LocationKind.Area => location.RowCode is null && location.RackNumber is null && location.PalletNumber is null &&
            IsValidAreaCode(location.Code),
        _ => false
    };

    [GeneratedRegex("^[A-Z]$", RegexOptions.CultureInvariant)]
    private static partial Regex RowCodePattern();

    [GeneratedRegex("^[A-Z0-9](?:[A-Z0-9-]*[A-Z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex AreaCodePattern();
}
