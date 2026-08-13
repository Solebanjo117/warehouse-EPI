using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Tests.Entities;

public sealed class EntityDefaultsTests
{
    [Fact]
    public void User_is_active_by_default()
    {
        var user = new User
        {
            FullName = "Operador de prueba",
            PinLookup = new string('a', 64),
            PinHash = "hash"
        };

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.True(user.IsActive);
    }

    [Fact]
    public void Product_allows_negative_stock_and_disables_lots_by_default()
    {
        var product = new Product
        {
            Sku = "SKU-001",
            Name = "Producto de prueba"
        };

        Assert.True(product.AllowsNegativeStock);
        Assert.True(product.IsActive);
        Assert.False(product.TracksLots);
        Assert.False(product.TracksExpiration);
    }

    [Fact]
    public void Location_accepts_existing_compact_codes_without_parsed_components()
    {
        var location = new Location { Code = "1A1" };

        Assert.Equal("1A1", location.Code);
        Assert.Null(location.Aisle);
        Assert.Null(location.Shelf);
        Assert.Null(location.LevelNumber);
        Assert.True(location.IsActive);
    }

    [Fact]
    public void Product_barcode_defaults_to_code_128()
    {
        var barcode = new ProductBarcode { Barcode = "1234567890" };

        Assert.Equal("CODE_128", barcode.Format);
        Assert.True(barcode.IsActive);
    }
}
