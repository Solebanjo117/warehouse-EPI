using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Reporting;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class OperationalAlertPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Migration_is_ordered_after_cycle_counts_and_supports_down_script()
    {
        await using var db = fixture.CreateDbContext();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var cycleIndex = Array.IndexOf(applied, "20260821120408_Phase135CycleCounts");
        var alertsIndex = Array.IndexOf(applied, "20260825190000_Phase136OperationalAlerts");
        var migrator = db.GetService<IMigrator>();
        var down = migrator.GenerateScript("20260825190000_Phase136OperationalAlerts", "20260825093000_AddWarehouseMapReferenceImages");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT column_default FROM information_schema.columns WHERE table_name = 'business_settings' AND column_name = 'wip_reminder_days'";
        await db.Database.OpenConnectionAsync();
        var defaultSql = Convert.ToString(await command.ExecuteScalarAsync());

        Assert.True(cycleIndex >= 0);
        Assert.True(alertsIndex > cycleIndex);
        Assert.Contains("7", defaultSql, StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN wip_reminder_days", down, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DROP CONSTRAINT ck_business_settings_wip_reminder_days", down, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_and_all_detail_queries_translate_on_postgresql()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-ALERT-{suffix}", $"PGA-{suffix}", "4386");
        await using var db = fixture.CreateDbContext();
        var product = await db.Products.SingleAsync(x => x.Id == seed.ProductId);
        var location = await db.Locations.SingleAsync(x => x.Id == seed.LocationId);
        var user = await db.Users.SingleAsync(x => x.FullName == $"Operador PG-ALERT-{suffix}");
        product.MinimumStock = 5m;
        location.IsBlocked = true;
        location.BlockReason = "Validación de alerta operacional";
        db.InventoryBalances.Add(new InventoryBalance { ProductId = product.Id, LocationId = location.Id, Quantity = -1m });
        var campaign = new CycleCountCampaign { OperationId = Guid.NewGuid(), Number = 900001, CreatedByUserId = user.Id };
        campaign.Locations.Add(new CycleCountLocation { LocationId = location.Id, Status = CycleCountLocationStatus.Stale });
        db.CycleCountCampaigns.Add(campaign);
        await db.SaveChangesAsync();
        var service = new OperationalAlertService(db, new WarehouseSettingsService(db),
            new WarehouseClock(new WarehouseSettingsService(db)), TimeProvider.System);

        var snapshot = await service.GetSnapshotAsync(OperationalAlertAudience.Admin);
        foreach (var category in Enum.GetValues<OperationalAlertCategory>())
            _ = await service.GetPageAsync(category, null, 1, 25);

        Assert.Contains(snapshot.Items, x => x.Category == OperationalAlertCategory.NegativeInventory);
        Assert.Contains(snapshot.Items, x => x.Category == OperationalAlertCategory.RestrictedInventory);
        Assert.Contains(snapshot.Items, x => x.Category == OperationalAlertCategory.CycleCountStale);
    }
}
