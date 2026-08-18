namespace WarehouseEPI.Infrastructure.Settings;

public sealed class WarehouseClock(WarehouseSettingsService settings)
{
    public async Task<DateTimeOffset> ConvertAsync(DateTimeOffset instant, CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAsync(cancellationToken);
        return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(current.TimeZoneId));
    }

    public async Task<DateOnly> GetDateAsync(DateTimeOffset instant, CancellationToken cancellationToken = default)
    {
        return DateOnly.FromDateTime((await ConvertAsync(instant, cancellationToken)).DateTime);
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
