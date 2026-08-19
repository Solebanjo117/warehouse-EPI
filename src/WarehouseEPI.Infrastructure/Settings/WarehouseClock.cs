namespace WarehouseEPI.Infrastructure.Settings;

public sealed class WarehouseClock(WarehouseSettingsService settings)
{
    public sealed record UtcInterval(DateTimeOffset? FromInclusive, DateTimeOffset? ToExclusive);

    public async Task<DateTimeOffset> ConvertAsync(DateTimeOffset instant, CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(current.TimeZoneId));
    }

    public async Task<DateOnly> GetDateAsync(DateTimeOffset instant, CancellationToken cancellationToken = default)
    {
        return DateOnly.FromDateTime((await ConvertAsync(instant, cancellationToken)).DateTime);
    }

    /// <summary>Converts warehouse calendar dates into a half-open UTC interval.</summary>
    public async Task<UtcInterval> GetUtcIntervalAsync(DateOnly? from, DateOnly? toInclusive, CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(current.TimeZoneId);
        return new(from is null ? null : ToUtcStart(from.Value, zone), toInclusive is null ? null : ToUtcStart(toInclusive.Value.AddDays(1), zone));
    }

    private static DateTimeOffset ToUtcStart(DateOnly date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static bool IsValidTimeZone(string timeZoneId)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
