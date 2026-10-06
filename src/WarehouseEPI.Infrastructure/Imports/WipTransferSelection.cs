namespace WarehouseEPI.Infrastructure.Imports;

/// <summary>Selects original rows without rebuilding their durable delivery identities.</summary>
public static class WipTransferSelection
{
    public static WipTransferFile Select(WipTransferFile file, string? search, int? delivery = null, DateOnly? from = null, DateOnly? to = null)
    {
        var term = search?.Trim() ?? "";
        var exact = file.Rows.Any(x => string.Equals(x.Sku, term, StringComparison.OrdinalIgnoreCase));
        return file with
        {
            Rows = file.Rows.Where(row =>
                (term.Length == 0 || (exact ? string.Equals(row.Sku, term, StringComparison.OrdinalIgnoreCase)
                    : row.Sku.Contains(term, StringComparison.OrdinalIgnoreCase))) &&
                (!delivery.HasValue || row.Number == delivery.Value) &&
                (!from.HasValue || row.Date >= from.Value) &&
                (!to.HasValue || row.Date <= to.Value)).ToArray()
        };
    }
}
