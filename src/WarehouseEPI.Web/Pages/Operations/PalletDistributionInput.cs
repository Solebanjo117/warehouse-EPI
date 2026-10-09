namespace WarehouseEPI.Web.Pages.Operations;

public sealed class PalletDistributionInput
{
    public bool Enabled { get; set; }
    public List<decimal?> Quantities { get; set; } = [null, null];
}

public sealed record PalletDistributionEditor(PalletDistributionInput Input, bool Optional,
    decimal Total = 0, string Unit = "", bool AllowsDecimals = true, bool CalculateTotal = false);
