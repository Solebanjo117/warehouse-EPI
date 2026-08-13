namespace WarehouseEPI.Core;

public static class CatalogNormalization
{
    public static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();

    public static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
