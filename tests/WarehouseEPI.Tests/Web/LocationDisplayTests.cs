using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Locations;

namespace WarehouseEPI.Tests.Web;

public sealed class LocationDisplayTests
{
    [Theory]
    [InlineData(null, "auto", 3)]
    [InlineData("invalid", "auto", 3)]
    [InlineData("auto", "auto", 3)]
    [InlineData("landscape", "landscape", 3)]
    [InlineData("portrait", "portrait", 1)]
    [InlineData("PORTRAIT", "portrait", 1)]
    public async Task Orientation_keeps_requested_racks_and_uses_effective_count_for_initial_and_refresh(
        string? orientation, string expected, int effective)
    {
        await using var db = CreateDb();
        for (short rack = 1; rack <= 4; rack++)
            db.Locations.Add(new Location { Code = $"A-{rack}-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = rack, PalletNumber = 1 });
        await db.SaveChangesAsync();
        var initial = CreateModel(db);
        await initial.OnGetAsync(["A"], 20, racks: 3, play: true, orientation: orientation);
        Assert.Equal(expected, initial.Orientation);
        Assert.Equal(3, initial.RacksPerScreen);
        Assert.Equal(effective, initial.EffectiveRacksPerScreen);
        Assert.Contains("&orientation=" + expected, initial.ConfigurationUrl);
        Assert.Contains("&racks=3", initial.ConfigurationUrl);
        Assert.All(initial.Slides, slide => Assert.InRange(slide.Racks.Count, 1, effective));
        Assert.Equal(effective == 1 ? 4 : 2, initial.Slides.Count);
        var refreshed = CreateModel(db);
        await refreshed.OnGetAsync(["A"], 20, racks: 3, play: true, refresh: true, orientation: orientation);
        Assert.Equal(initial.Slides.Select(slide => slide.RacksLabel), refreshed.Slides.Select(slide => slide.RacksLabel));
        Assert.Equal(initial.Slides.Select(slide => slide.Part), refreshed.Slides.Select(slide => slide.Part));
    }

    [Theory]
    [InlineData("asc", 1)]
    [InlineData("desc", 1)]
    [InlineData("desc", 2)]
    [InlineData("desc", 3)]
    [InlineData("DESC", 2)]
    [InlineData("invalid", 2)]
    [InlineData(null, 1)]
    public async Task Display_order_is_preserved_in_configuration_and_refresh(string? order, int racks)
    {
        await using var db = CreateDb();
        foreach (var row in new[] { "A", "B" })
            for (short rack = 1; rack <= 4; rack++)
                db.Locations.Add(new Location { Code = $"{row}-{rack}-1", Kind = LocationKind.Rack, RowCode = row, RackNumber = rack, PalletNumber = 1 });
        await db.SaveChangesAsync();
        var ascending = CreateModel(db);
        await ascending.OnGetAsync(["A", "B"], 20, racks: 1, play: true);
        var expected = ascending.Slides.SelectMany(slide => slide.Racks).Select(rack => rack.Label).ToArray();
        var descending = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);
        if (descending) Array.Reverse(expected);
        var model = CreateModel(db);
        await model.OnGetAsync(["A", "B"], 20, racks: racks, play: true, order: order);
        Assert.Equal(expected, model.Slides.SelectMany(slide => slide.Racks).Select(rack => rack.Label));
        Assert.Equal(descending ? "desc" : "asc", model.Order);
        Assert.Contains("&order=" + model.Order, model.ConfigurationUrl);
        Assert.All(model.Slides, slide => Assert.InRange(slide.Racks.Count, 1, racks));
        foreach (var row in new[] { "A", "B" })
        {
            var refresh = CreateModel(db);
            await refresh.OnGetAsync([row], 20, racks: racks, play: true, refresh: true, order: order);
            var initial = model.Slides.Where(slide => slide.RowCode == row).ToArray();
            Assert.Equal(initial.Select(slide => slide.RacksLabel), refresh.Slides.Select(slide => slide.RacksLabel));
            Assert.Equal(initial.Select(slide => slide.Part), refresh.Slides.Select(slide => slide.Part));
        }
    }

    [Fact]
    public async Task Selected_rows_keep_nine_positions_in_keypad_order_and_net_stock_per_product()
    {
        await using var db = CreateDb();
        var unit = await db.Units.FirstAsync();
        var product = new Product { Sku = "NET", BaseUnitId = unit.Id };
        var location = new Location { Code = "A-1-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 1, PalletNumber = 1 };
        db.AddRange(product, location);
        db.InventoryBalances.AddRange(new InventoryBalance { Product = product, Location = location, Quantity = 2 },
            new InventoryBalance { Product = product, Location = location, Quantity = -1 });
        db.Locations.AddRange(
            new Location { Code = "A-2-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 2, PalletNumber = 1 },
            new Location { Code = "B-1-1", Kind = LocationKind.Rack, RowCode = "B", RackNumber = 1, PalletNumber = 1 });
        await db.SaveChangesAsync();
        var model = CreateModel(db);
        await model.OnGetAsync(["A"], 8, racks: 1, play: true);
        Assert.Equal(2, model.Slides.Count);
        Assert.All(model.Slides, slide => Assert.Equal("A", slide.RowCode));
        var rack = model.Slides.SelectMany(slide => slide.Racks).Single(rack => rack.RackNumber == 1);
        Assert.Equal(new short[] { 7, 8, 9, 4, 5, 6, 1, 2, 3 }, rack.Positions.Select(position => position.PalletNumber));
        var occupied = Assert.Single(rack.Positions, position => position.State == "occupied");
        Assert.Equal(1, Assert.Single(occupied.Products).Quantity);
        Assert.Equal(8, rack.Positions.Count(position => position.State == "missing"));
        Assert.Equal(model.Slides[1].Racks[0].Label, model.Slides[0].RightNeighbor);
        Assert.Equal(model.Slides[0].Racks[0].Label, model.Slides[1].LeftNeighbor);
        Assert.Contains("racks=1", model.ConfigurationUrl);
    }

    [Fact]
    public async Task Saved_geometry_controls_order_and_hidden_racks_stay_out_of_context_map()
    {
        await using var db = CreateDb();
        for (short rack = 1; rack <= 3; rack++)
            db.Locations.Add(new Location { Code = $"A-{rack}-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = rack, PalletNumber = 1 });
        db.WarehouseMapLayouts.Add(new WarehouseMapLayout { Elements = [
            new WarehouseMapElement { Kind = WarehouseMapElementKind.Rack, RowCode = "A", RackNumber = 1, X = 725, Y = 340, Width = 90, Height = 42, Rotation = 90, IsVisible = true },
            new WarehouseMapElement { Kind = WarehouseMapElementKind.Rack, RowCode = "A", RackNumber = 2, X = 300, Y = 340, Width = 90, Height = 42, IsVisible = true },
            new WarehouseMapElement { Kind = WarehouseMapElementKind.Rack, RowCode = "A", RackNumber = 3, X = 100, Y = 340, Width = 90, Height = 42, IsVisible = false }] });
        await db.SaveChangesAsync();
        var model = CreateModel(db);
        await model.OnGetAsync(["A"], 20, racks: 3, play: true);
        Assert.Equal(new short[] { 2, 1, 3 }, Assert.Single(model.Slides).Racks.Select(rack => rack.RackNumber));
        Assert.NotNull(model.ContextMap);
        Assert.Equal(2, model.ContextMap.Elements.Count);
        var placed = Assert.Single(model.ContextMap.Elements, item => item.Label == "A-1");
        Assert.Equal(725, placed.X);
        Assert.Equal((short)90, placed.Rotation);
        Assert.DoesNotContain("NaN", model.ContextMap.ViewBox);
    }

    [Fact]
    public async Task Refresh_keeps_empty_slots_and_updates_negative_stock_without_loading_static_map()
    {
        await using var db = CreateDb();
        var unit = await db.Units.FirstAsync();
        var product = new Product { Sku = "REFRESH", BaseUnitId = unit.Id };
        var location = new Location { Code = "A-1-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 1, PalletNumber = 1 };
        var balance = new InventoryBalance { Product = product, Location = location, Quantity = 3 };
        db.AddRange(product, location, balance);
        await db.SaveChangesAsync();
        balance.Quantity = -4;
        await db.SaveChangesAsync();
        var model = CreateModel(db);
        await model.OnGetAsync(["A"], 20, play: true, refresh: true);
        Assert.Null(model.ContextMap);
        var position = Assert.Single(Assert.Single(model.Slides).Racks).Positions.Single(position => position.Code == "A-1-1");
        Assert.Equal("negative", position.State);
        Assert.Equal(-4, Assert.Single(position.Products).Quantity);
        balance.Quantity = 0;
        await db.SaveChangesAsync();
        await model.OnGetAsync(["A"], 20, play: true, refresh: true);
        Assert.Equal("empty", Assert.Single(Assert.Single(model.Slides).Racks).Positions.Single(position => position.Code == "A-1-1").State);
    }

    [Fact]
    public async Task Empty_selection_does_not_start_carousel()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);
        await model.OnGetAsync([], 20, play: true);
        Assert.False(model.Play);
        Assert.NotNull(model.Error);
        Assert.Equal(1, model.RacksPerScreen);
    }

    [Theory]
    [InlineData(1, "auto", "es")]
    [InlineData(1, "auto", "en")]
    [InlineData(2, "auto", "es")]
    [InlineData(2, "auto", "en")]
    [InlineData(3, "auto", "es")]
    [InlineData(3, "auto", "en")]
    [InlineData(1, "landscape", "es")]
    [InlineData(1, "landscape", "en")]
    [InlineData(2, "landscape", "es")]
    [InlineData(2, "landscape", "en")]
    [InlineData(3, "landscape", "es")]
    [InlineData(3, "landscape", "en")]
    [InlineData(1, "portrait", "es")]
    [InlineData(1, "portrait", "en")]
    [InlineData(2, "portrait", "es")]
    [InlineData(2, "portrait", "en")]
    [InlineData(3, "portrait", "es")]
    [InlineData(3, "portrait", "en")]
    public async Task Public_display_renders_racks_and_configuration_assets(int racks, string orientation, string language)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var unit = await db.Units.FirstAsync();
        for (short rack = 1; rack <= 3; rack++)
        {
            for (short pallet = 1; pallet <= 9; pallet++)
            {
                var location = new Location { Code = $"A-{rack}-{pallet}", Kind = LocationKind.Rack, RowCode = "A", RackNumber = rack, PalletNumber = pallet };
                db.Add(location);
                if (pallet % 3 != 0)
                {
                    var product = new Product { Sku = $"V45-E-50UE-12M-SHX08-AB-CN-{rack}-{pallet}", Description = "Material de prueba para exhibición", BaseUnitId = unit.Id };
                    db.InventoryBalances.Add(new InventoryBalance { Location = location, Product = product, Quantity = pallet == 2 ? -2 : 1250 });
                }
            }
        }
        db.Locations.Add(new Location { Code = "B-1-1", Kind = LocationKind.Rack, RowCode = "B", RackNumber = 1, PalletNumber = 1 });
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"WarehouseEPI.Language={language}");
        var config = await client.GetStringAsync("/Locations/Display");
        Assert.Contains("location-display-config", config);
        Assert.Contains("data-display-here-map", config);
        Assert.Contains("name=\"order\"", config);
        Assert.Contains("name=\"orientation\"", config);
        Assert.Contains("value=\"auto\" checked=\"checked\"", config);
        Assert.Contains("data-display-preview", config);
        Assert.Contains("value=\"asc\" selected=\"selected\"", config);
        var descendingConfig = await client.GetStringAsync("/Locations/Display?rows=A&order=desc");
        Assert.Contains("value=\"desc\" selected=\"selected\"", descendingConfig);
        var configured = await client.GetStringAsync($"/Locations/Display?rows=A&racks={racks}&orientation={orientation}");
        Assert.Contains($"value=\"{orientation}\" checked=\"checked\"", configured);
        var html = await client.GetStringAsync($"/Locations/Display?play=true&rows=A&rows=B&racks={racks}&here=500,300&orientation={orientation}");
        var effective = orientation == "portrait" ? 1 : racks;
        Assert.Contains("display-rack-level", html);
        Assert.Contains("data-display-row-jump=\"B\"", html);
        Assert.Contains("data-display-map-svg", html);
        Assert.Contains("is-negative", html);
        Assert.DoesNotContain("Material de prueba para exhibición", System.Net.WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("display-cell-desc", html);
        Assert.Contains("/js/location-display.", html);
        Assert.Equal(effective == 1, html.Contains("display-stage-split", StringComparison.Ordinal));
        Assert.Contains("data-display-map-caption", html);
        Assert.Contains($"data-display-grouped=\"{(effective > 1 ? "true" : "false")}\"", html);
        Assert.Contains($"data-display-orientation=\"{orientation}\"", html);
        Assert.Contains($"data-racks-per-screen=\"{effective}\"", html);
        Assert.Contains($"orientation={orientation}", html);
        Assert.Contains("data-display-rack=\"A-3\"", html);
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_DISPLAY_FIXTURES");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var directories = language == "es" ? new[] { directory, Path.Combine(directory, language) } : [Path.Combine(directory, language)];
            foreach (var target in directories) Directory.CreateDirectory(target);
            async Task WriteFixtureAsync(string name, string content)
            {
                foreach (var target in directories) await File.WriteAllTextAsync(Path.Combine(target, name), content);
            }
            var suffix = orientation == "auto" ? "" : "-" + orientation;
            await WriteFixtureAsync($"display-{racks}{suffix}.html", html);
            if (racks == 2 && orientation == "auto") await WriteFixtureAsync("display.html", html);
            if (racks == 2 && orientation == "auto") await WriteFixtureAsync("config.html", config);
            await WriteFixtureAsync($"config-{racks}-{orientation}.html", configured);
            foreach (var row in new[] { "A", "B" })
            {
                var refreshed = await client.GetStringAsync($"/Locations/Display?play=true&rows={row}&racks={racks}&refresh=true&orientation={orientation}");
                await WriteFixtureAsync($"refresh-{racks}-{row}{suffix}.html", refreshed);
                if (racks == 2 && orientation == "auto") await WriteFixtureAsync($"refresh-{row}.html", refreshed);
            }
        }
    }

    private static DisplayModel CreateModel(WarehouseDbContext db) =>
        new(db, new WarehouseClock(new WarehouseSettingsService(db)), new WarehouseMapService(db));

    private static WarehouseDbContext CreateDb()
    {
        var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase($"LocationDisplay-{Guid.NewGuid():N}").Options);
        db.Database.EnsureCreated();
        return db;
    }
}
