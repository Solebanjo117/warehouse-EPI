using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Settings;

public sealed record BusinessSettingsSnapshot(
    string BusinessName,
    string WarehouseName,
    string WarehouseCode,
    string TimeZoneId,
    int WipReminderDays,
    string? LogoFileName,
    string? LogoContentType,
    string? LogoHash,
    DateTimeOffset UpdatedAt);

public sealed class WarehouseSettingsService(WarehouseDbContext dbContext)
{
    public const string DefaultBusinessName = "EPI";
    public const string DefaultWarehouseName = "Almacén principal";
    public const string DefaultWarehouseCode = "EPI";
    public const string DefaultTimeZoneId = "America/Matamoros";

    public async Task<BusinessSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await dbContext.BusinessSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == BusinessSettings.SingletonId, cancellationToken);
            return settings is null ? Defaults() : Snapshot(settings);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // The release may be deployed before its reviewed migration is applied.
            return Defaults();
        }
    }

    public async Task<BusinessSettings> GetTrackedAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.BusinessSettings
            .SingleOrDefaultAsync(item => item.Id == BusinessSettings.SingletonId, cancellationToken);
        if (settings is not null)
            return settings;

        settings = new BusinessSettings
        {
            BusinessName = DefaultBusinessName,
            WarehouseName = DefaultWarehouseName,
            WarehouseCode = DefaultWarehouseCode,
            TimeZoneId = DefaultTimeZoneId,
            UpdatedByUserId = Guid.Empty
        };
        dbContext.BusinessSettings.Add(settings);
        return settings;
    }

    public static BusinessSettingsSnapshot Defaults() => new(
        DefaultBusinessName,
        DefaultWarehouseName,
        DefaultWarehouseCode,
        DefaultTimeZoneId,
        7,
        null,
        null,
        null,
        DateTimeOffset.UnixEpoch);

    public static BusinessSettingsSnapshot Snapshot(BusinessSettings settings) => new(
        settings.BusinessName,
        settings.WarehouseName,
        settings.WarehouseCode,
        settings.TimeZoneId,
        settings.WipReminderDays,
        settings.LogoFileName,
        settings.LogoContentType,
        settings.LogoHash,
        settings.UpdatedAt);
}
