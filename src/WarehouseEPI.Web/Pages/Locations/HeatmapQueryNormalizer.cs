using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Locations;

internal sealed record HeatmapQueryState(
    string MapMetric,
    string Period,
    DateOnly? From,
    DateOnly? To,
    HeatmapReportFilter Filter,
    string PeriodLabel);

internal static class HeatmapQueryNormalizer
{
    public static bool IsCanonicalMetric(string? value)
        => value is "occupancy" or "activity";

    public static string NormalizeLegacyMetric(string? value, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "access-frequency" or "activity" => "activity",
            "occupancy-density" or "occupancy" => "occupancy",
            _ => fallback
        };
    }

    public static async Task<HeatmapQueryState> BuildAsync(
        string mapMetric,
        string? period,
        DateOnly? from,
        DateOnly? to,
        WarehouseClock clock,
        string? rowCode = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsCanonicalMetric(mapMetric))
            throw new ArgumentOutOfRangeException(nameof(mapMetric));

        var metric = mapMetric == "activity"
            ? HeatmapMetricType.AccessFrequency
            : HeatmapMetricType.OccupancyDensity;
        var periodCandidate = period?.Trim().ToLowerInvariant();
        var normalizedPeriod = periodCandidate is "7" or "14" or "30" or "custom"
            ? periodCandidate
            : "30";
        var normalizedRow = string.IsNullOrWhiteSpace(rowCode) ? null : rowCode.Trim();
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        if (metric == HeatmapMetricType.OccupancyDensity)
        {
            return new HeatmapQueryState(
                mapMetric,
                normalizedPeriod,
                from,
                to,
                new HeatmapReportFilter(metric, RowCode: normalizedRow, Search: normalizedSearch, PageSize: 50),
                "Saldo actual en estantería");
        }

        var today = await clock.GetDateAsync(TimeProvider.System.GetUtcNow(), cancellationToken);
        DateOnly normalizedFrom;
        DateOnly normalizedTo;
        if (normalizedPeriod == "custom" && from.HasValue && to.HasValue)
        {
            normalizedFrom = from.Value <= to.Value ? from.Value : to.Value;
            normalizedTo = from.Value <= to.Value ? to.Value : from.Value;
        }
        else
        {
            var days = normalizedPeriod == "7" ? 7 : normalizedPeriod == "14" ? 14 : 30;
            normalizedFrom = today.AddDays(-(days - 1));
            normalizedTo = today;
            normalizedPeriod = days.ToString();
        }

        var interval = await clock.GetUtcIntervalAsync(normalizedFrom, normalizedTo, cancellationToken);
        var label = normalizedPeriod == "custom"
            ? $"{normalizedFrom:dd/MM/yyyy} a {normalizedTo:dd/MM/yyyy}"
            : $"Últimos {normalizedPeriod} días";
        return new HeatmapQueryState(
            mapMetric,
            normalizedPeriod,
            normalizedFrom,
            normalizedTo,
            new HeatmapReportFilter(
                metric,
                interval.FromInclusive,
                interval.ToExclusive,
                normalizedRow,
                normalizedSearch,
                PageSize: 50),
            label);
    }
}
