using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Locations;

namespace WarehouseEPI.Tests.Web;

public sealed class LocationDisplayTests
{
    [Fact]
    public async Task Selected_row_uses_net_stock_and_splits_without_mixing_rows()
    {
        await using var db = CreateDb();
        var unit = await db.Units.FirstAsync();
        for (var index = 1; index <= 13; index++)
        {
            var rack = (short)(index <= 9 ? 1 : 2);
            var pallet = (short)(index <= 9 ? index : index - 9);
            var location = new Location { Code = $"A-{rack}-{pallet}", Kind = LocationKind.Rack,
                RowCode = "A", RackNumber = rack, PalletNumber = pallet };
            var product = new Product { Sku = $"SKU-{index:00}", BaseUnitId = unit.Id };
            db.AddRange(location, product);
            db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = location, Quantity = index == 1 ? 2 : index });
            if (index == 1)
                db.InventoryBalances.Add(new InventoryBalance { Product = product, Location = location, Quantity = -1 });
        }
        var otherLocation = new Location { Code = "B-1-1", Kind = LocationKind.Rack,
            RowCode = "B", RackNumber = 1, PalletNumber = 1 };
        var otherProduct = new Product { Sku = "OTHER", BaseUnitId = unit.Id };
        db.AddRange(otherLocation, otherProduct);
        db.InventoryBalances.Add(new InventoryBalance { Product = otherProduct, Location = otherLocation, Quantity = -3 });
        await db.SaveChangesAsync();

        var model = new DisplayModel(db, new WarehouseClock(new WarehouseSettingsService(db)));
        await model.OnGetAsync(["A"], 8, play: true);

        Assert.True(model.Play);
        Assert.Equal(8, model.Seconds);
        Assert.Equal(2, model.Slides.Count);
        Assert.Equal([12, 1], model.Slides.Select(slide => slide.Stocks.Count));
        Assert.All(model.Slides, slide => Assert.Equal("A", slide.RowCode));
        Assert.Equal(1, Assert.Single(model.Slides.SelectMany(slide => slide.Stocks), stock => stock.Sku == "SKU-01").Quantity);

        await model.OnGetAsync(["B"], 8, play: true);
        Assert.Equal(-3, Assert.Single(Assert.Single(model.Slides).Stocks).Quantity);
    }

    [Fact]
    public async Task Empty_selection_does_not_start_carousel()
    {
        await using var db = CreateDb();
        var model = new DisplayModel(db, new WarehouseClock(new WarehouseSettingsService(db)));
        await model.OnGetAsync([], 20, play: true);
        Assert.False(model.Play);
        Assert.NotNull(model.Error);
    }

    private static WarehouseDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"LocationDisplay-{Guid.NewGuid():N}").Options;
        var db = new WarehouseDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }
}
