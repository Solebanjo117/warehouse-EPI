using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Reporting;

public sealed record WipReportSelection(string? Attention, string Sort)
{
    public static WipReportSelection FromQuery(string? attention, string? sort)
    {
        var normalized = attention?.ToLowerInvariant() is "pending" or "aged" ? attention.ToLowerInvariant() : null;
        return new(normalized, sort is "oldest" or "newest" ? sort : normalized is null ? "newest" : "oldest");
    }

    public WipReportFilter Apply(WipReportFilter filter, DateTimeOffset now, int reminderDays) => filter with
    {
        PendingOnly = Attention is not null,
        AgedBefore = Attention == "aged" ? now.AddDays(-reminderDays) : null,
        OldestFirst = Sort == "oldest"
    };
}
