using System.Diagnostics;

namespace WarehouseEPI.Infrastructure.Production;

// No listener means no activities are allocated. Contains phase names only, never command data or PINs.
internal static class ProductionBalanceDiagnostics
{
    internal static readonly ActivitySource Source = new("WarehouseEPI.Production.Balance");
}
