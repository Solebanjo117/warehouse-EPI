namespace WarehouseEPI.Core;

/// <summary>Physical elevation, numbered from the bottom left without changing location codes.</summary>
public sealed record RackFormat(int Columns = 3, int Levels = 3)
{
    public static RackFormat Default { get; } = new();
    public bool IsValid => Columns is >= 1 and <= 3 && Levels is >= 1 and <= 3;
    public int Capacity => Columns * Levels;
    public IReadOnlyList<short> PalletOrder => IsValid
        ? Enumerable.Range(0, Levels).Reverse()
            .SelectMany(level => Enumerable.Range(1, Columns).Select(column => (short)(level * Columns + column)))
            .ToArray()
        : Default.PalletOrder;
}
