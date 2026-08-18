using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Tests.Settings;

public sealed class WarehouseSettingsTests
{
    [Fact]
    public async Task Defaults_preserve_the_existing_warehouse_identity_and_timezone()
    {
        await using var db = CreateContext();
        var settings = await new WarehouseSettingsService(db).GetAsync();

        Assert.Equal("EPI", settings.BusinessName);
        Assert.Equal("Almacén principal", settings.WarehouseName);
        Assert.Equal("America/Matamoros", settings.TimeZoneId);
    }

    [Fact]
    public async Task Clock_uses_configured_timezone_at_the_midnight_boundary()
    {
        await using var db = CreateContext();
        db.BusinessSettings.Add(new BusinessSettings
        {
            BusinessName = "EPI",
            WarehouseName = "Almacén principal",
            WarehouseCode = "EPI",
            TimeZoneId = "America/Matamoros",
            UpdatedByUserId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        var clock = new WarehouseClock(new WarehouseSettingsService(db));
        var beforeMidnight = await clock.GetDateAsync(new DateTimeOffset(2026, 8, 18, 4, 30, 0, TimeSpan.Zero));
        var afterMidnight = await clock.GetDateAsync(new DateTimeOffset(2026, 8, 18, 5, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 8, 17), beforeMidnight);
        Assert.Equal(new DateOnly(2026, 8, 18), afterMidnight);
    }

    [Fact]
    public void Timezone_validation_rejects_unknown_identifiers()
    {
        Assert.True(WarehouseClock.IsValidTimeZone("America/Matamoros"));
        Assert.False(WarehouseClock.IsValidTimeZone("Warehouse/NoExiste"));
    }

    private static WarehouseDbContext CreateContext() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);
}
