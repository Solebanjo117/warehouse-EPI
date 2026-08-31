using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Tests.Inventory;

namespace WarehouseEPI.Tests.Reporting;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class OperationalExceptionPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Migration_and_reconciliation_use_postgresql_indexes_and_xmin()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-EXCEPTION-{suffix}", $"PEX-{suffix}", "4386");
        await using var db = fixture.CreateDbContext();
        db.InventoryBalances.Add(new InventoryBalance { ProductId = seed.ProductId, LocationId = seed.LocationId, Quantity = -1m });
        await db.SaveChangesAsync();
        var settings = new WarehouseSettingsService(db);
        var service = new OperationalExceptionService(db,
            new OperationalAlertService(db, settings, new WarehouseClock(settings), TimeProvider.System), TimeProvider.System);

        var result = await service.ReconcileAsync();
        var page = await service.GetPageAsync(new(Category: OperationalExceptionCategory.NegativeInventory));
        var migrator = db.GetService<IMigrator>();
        var down = migrator.GenerateScript("20260828143458_AddOperationalExceptionCenter", "20260828120000_WipTrackedInventory");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_indexes WHERE tablename = 'operational_exception_cases' AND indexname = 'IX_operational_exception_cases_category_condition_key'";
        await db.Database.OpenConnectionAsync();
        var indexCount = Convert.ToInt32(await command.ExecuteScalarAsync());

        Assert.True(result.Created > 0);
        Assert.Contains(page.Items, item => item.PrimaryText == $"PG-EXCEPTION-{suffix}" && item.Version > 0);
        Assert.Equal(1, indexCount);
        Assert.Contains("DROP TABLE operational_exception_events", down, StringComparison.OrdinalIgnoreCase);

        var balance = await db.InventoryBalances.SingleAsync(item =>
            item.ProductId == seed.ProductId && item.LocationId == seed.LocationId);
        balance.Quantity = 0m;
        await db.SaveChangesAsync();
        var resolution = await service.ReconcileAsync();
        var resolvedPage = await service.GetPageAsync(new(
            Status: OperationalExceptionStatus.Resolved,
            Category: OperationalExceptionCategory.NegativeInventory));
        var detail = await service.GetDetailAsync(resolvedPage.Items.Single(item => item.PrimaryText == $"PG-EXCEPTION-{suffix}").Id);

        Assert.True(resolution.Resolved > 0);
        Assert.Contains(detail!.Events, item => item.Type == OperationalExceptionEventType.AutoResolved);
    }

    [Fact]
    public async Task Reconciliation_preserves_long_product_descriptions()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var seed = await fixture.SeedAsync($"PG-LONG-{suffix}", $"PLG-{suffix}", "4387");
        await using var db = fixture.CreateDbContext();
        var product = await db.Products.SingleAsync(item => item.Id == seed.ProductId);
        product.Description = new string('D', 260);
        product.MinimumStock = 1m;
        await db.SaveChangesAsync();
        var settings = new WarehouseSettingsService(db);
        var service = new OperationalExceptionService(db,
            new OperationalAlertService(db, settings, new WarehouseClock(settings), TimeProvider.System), TimeProvider.System);

        await service.ReconcileAsync();
        var page = await service.GetPageAsync(new(Category: OperationalExceptionCategory.BelowMinimum));

        Assert.Contains(page.Items, item => item.PrimaryText == $"PG-LONG-{suffix}" &&
            item.SecondaryText.Length == 200 && item.SecondaryText.EndsWith('…'));
    }
}
