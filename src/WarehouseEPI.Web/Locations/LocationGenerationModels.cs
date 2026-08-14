namespace WarehouseEPI.Web.Locations;

public sealed record LocationGenerationRow(string Code, string RowCode, short RackNumber, short PalletNumber, bool Exists)
{
    public string Level => WarehouseEPI.Core.LocationNormalization.LevelName(PalletNumber);
}

public sealed record LocationGenerationPreview(
    string Token,
    Guid OwnerUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Manifest,
    IReadOnlyList<LocationGenerationRow> Rows,
    IReadOnlyList<string> Errors)
{
    public bool CanConfirm => Errors.Count == 0 && Rows.Count > 0 && Rows.All(row => !row.Exists);
    public int RackCount => Rows.Select(row => (row.RowCode, row.RackNumber)).Distinct().Count();
    public int ExistingCount => Rows.Count(row => row.Exists);
}

public sealed record LocationGenerationConfirmation(bool Succeeded, int Inserted, string? ErrorMessage = null);
