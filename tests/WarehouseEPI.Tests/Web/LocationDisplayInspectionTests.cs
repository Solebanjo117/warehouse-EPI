using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Web;

public sealed class LocationDisplayInspectionTests
{
    [Theory]
    [InlineData("es", "Asignado sin saldo", "Consultar rack B-2")]
    [InlineData("en", "Assigned without stock", "View rack B-2")]
    public async Task Public_lookup_finds_all_locations_and_full_contents_without_changing_the_carousel(
        string language, string assignedLabel, string rackLabel)
    {
        using var original = new AdminRouteTests.WarehouseApplicationFactory();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" })));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var unit = await db.Units.FirstAsync();
        var product = new Product { Sku = "LOOKUP", Description = "Descripción buscable", ExternalReference = "REF-LOOKUP", BaseUnitId = unit.Id, IsActive = false };
        var a = new Location { Code = "A-1-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 1, PalletNumber = 1 };
        var next = new Location { Code = "A-2-1", Kind = LocationKind.Rack, RowCode = "A", RackNumber = 2, PalletNumber = 1 };
        var cancelled = new Location { Code = "C-3-5", Kind = LocationKind.Rack, RowCode = "C", RackNumber = 3, PalletNumber = 5 };
        var b = new Location { Code = "B-2-8", Kind = LocationKind.Rack, RowCode = "B", RackNumber = 2, PalletNumber = 8, IsActive = false, IsBlocked = true };
        var area = new Location { Code = "SHIPPING", Kind = LocationKind.Area };
        var retired = new Location { Code = "RETIRED", IsPhysicallyPresent = false };
        db.AddRange(product, a, next, cancelled, b, area, retired);
        db.ProductBarcodes.Add(new ProductBarcode { Product = product, Barcode = "123456789" });
        db.InventoryBalances.AddRange(new InventoryBalance { Product = product, Location = a, Quantity = 4 },
            new InventoryBalance { Product = product, Location = a, Quantity = -4 },
            new InventoryBalance { Product = product, Location = cancelled, Quantity = 6 },
            new InventoryBalance { Product = product, Location = cancelled, Quantity = -6 },
            new InventoryBalance { Product = product, Location = b, Quantity = 2 },
            new InventoryBalance { Product = product, Location = b, Quantity = -5 },
            new InventoryBalance { Product = product, Location = area, Quantity = 12 },
            new InventoryBalance { Product = product, Location = retired, Quantity = 99 });
        db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = product, Location = a });
        for (var i = 0; i < 6; i++)
        {
            var extra = new Product { Sku = $"EXTRA-{i}", Description = $"Descripción completa {i}", BaseUnitId = unit.Id };
            db.InventoryBalances.Add(new InventoryBalance { Product = extra, Location = b, Quantity = i + 1 });
        }
        await db.SaveChangesAsync();
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("Cookie", $"WarehouseEPI.Language={language}");
        const string route = "/Locations/Display?handler=";
        foreach (var term in new[] { "lookup", "123456789", "buscable", "REF-LOOKUP" })
        {
            using var found = JsonDocument.Parse(await client.GetStringAsync(route + "Products&q=" + term));
            Assert.Equal("LOOKUP", Assert.Single(found.RootElement.EnumerateArray()).GetProperty("sku").GetString());
        }
        using var partial = JsonDocument.Parse(await client.GetStringAsync(route + "Products&q=EXTRA"));
        Assert.Equal(5, partial.RootElement.GetArrayLength());
        using var empty = JsonDocument.Parse(await client.GetStringAsync(route + "Products&q=does-not-exist"));
        Assert.Equal(0, empty.RootElement.GetArrayLength());
        using var result = await client.GetAsync(route + $"ProductLocations&productId={product.Id}");
        Assert.Contains("no-store", result.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var locationJson = await result.Content.ReadAsStringAsync();
        using var locations = JsonDocument.Parse(locationJson);
        var rows = locations.RootElement.GetProperty("locations").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        var zero = Assert.Single(rows, item => item.GetProperty("code").GetString() == a.Code);
        Assert.Equal(0, zero.GetProperty("quantity").GetDecimal());
        Assert.True(zero.GetProperty("hasActiveAssignment").GetBoolean());
        Assert.False(zero.GetProperty("hasNonZeroBalance").GetBoolean());
        var negative = Assert.Single(rows, item => item.GetProperty("code").GetString() == b.Code);
        Assert.Equal(-3, negative.GetProperty("quantity").GetDecimal());
        Assert.True(negative.GetProperty("isBlocked").GetBoolean());
        Assert.False(negative.GetProperty("isActive").GetBoolean());
        var inspectHtml = await client.GetStringAsync(route + "Inspect&rowCode=B&rackNumber=2");
        var inspect = WebUtility.HtmlDecode(inspectHtml);
        Assert.Contains("B-2-8", inspect);
        Assert.Contains("Descripción completa 5", inspect);
        Assert.Contains("EXTRA-5", inspect);
        Assert.Contains("class=\"display-heading\"", inspect);
        Assert.Contains("class=\"display-sign\"", inspect);
        Assert.Contains("class=\"display-cell-sku\"", inspect);
        Assert.Contains("class=\"display-cell-qty ", inspect);
        Assert.DoesNotContain("productos más", inspect);
        Assert.Equal(9, inspect.Split("class=\"display-cell is-", StringSplitOptions.None).Length - 1);
        var assigned = WebUtility.HtmlDecode(await client.GetStringAsync(route + $"Inspect&locationId={a.Id}"));
        Assert.Contains(assignedLabel, assigned);
        var areaHtml = await client.GetStringAsync(route + $"Inspect&locationId={area.Id}");
        Assert.Contains("SHIPPING", areaHtml);
        Assert.Contains("LOOKUP", areaHtml);
        var displayHtml = await client.GetStringAsync("/Locations/Display?play=true&rows=A");
        var display = WebUtility.HtmlDecode(displayHtml);
        Assert.Contains(rackLabel, display);
        Assert.Contains("role=\"combobox\"", display);
        Assert.Contains("class=\"lookup-results list-group\"", display);
        Assert.DoesNotContain("data-display-rack=\"B-2\"", display);
        foreach (var query in new[] { "Inspect&locationId=" + retired.Id, "Inspect&rowCode=Z&rackNumber=99", "ProductLocations&productId=" + Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route + query)).StatusCode);
        foreach (var query in new[] { "Inspect&rowCode=B&rackNumber=0", "ProductLocations", "Inspect&locationId=" + Guid.Empty })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + query)).StatusCode);
        var directory = Environment.GetEnvironmentVariable("WAREHOUSE_DISPLAY_FIXTURES");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var target = Path.Combine(directory, language, "interactive");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "display.html"), displayHtml);
            await File.WriteAllTextAsync(Path.Combine(target, "display-grouped.html"), await client.GetStringAsync("/Locations/Display?play=true&rows=A&racks=2"));
            await File.WriteAllTextAsync(Path.Combine(target, "locations.json"), locationJson);
            await File.WriteAllTextAsync(Path.Combine(target, "products.json"), await client.GetStringAsync(route + "Products&q=LOOKUP"));
            await File.WriteAllTextAsync(Path.Combine(target, "partial-products.json"), partial.RootElement.GetRawText());
            await File.WriteAllTextAsync(Path.Combine(target, "rack.html"), inspectHtml);
            await File.WriteAllTextAsync(Path.Combine(target, "assigned.html"), assigned);
            await File.WriteAllTextAsync(Path.Combine(target, "area.html"), areaHtml);
        }
    }
}
