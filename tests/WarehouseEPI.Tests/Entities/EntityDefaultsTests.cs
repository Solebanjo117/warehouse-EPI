using WarehouseEPI.Core.Entities;
using WarehouseEPI.Core;

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
            Sku = "SKU-001"
        };

        Assert.True(product.AllowsNegativeStock);
        Assert.True(product.IsActive);
        Assert.False(product.TracksLots);
        Assert.False(product.TracksExpiration);
    }

    [Fact]
    public void Location_defaults_to_available_area_structure()
    {
        var location = new Location { Code = "SHIPPING", Kind = LocationKind.Area };

        Assert.Equal("SHIPPING", location.Code);
        Assert.Null(location.RowCode);
        Assert.Null(location.RackNumber);
        Assert.Null(location.LevelNumber);
        Assert.True(location.IsOperational);
        Assert.True(location.IsActive);
    }

    [Fact]
    public void Product_barcode_defaults_to_code_128()
    {
        var barcode = new ProductBarcode { Barcode = "1234567890" };

        Assert.Equal("CODE_128", barcode.Format);
        Assert.True(barcode.IsActive);
    }

    [Theory]
    [InlineData(" sku-001 ", "SKU-001")]
    [InlineData("raw material", "RAW MATERIAL")]
    public void Catalog_codes_are_trimmed_and_uppercase(string value, string expected)
    {
        Assert.Equal(expected, CatalogNormalization.NormalizeCode(value));
    }

    [Fact]
    public void Product_supports_optional_catalogs_and_external_reference()
    {
        var product = new Product
        {
            Sku = "SKU-001",
            ExternalReference = "RM:SKU-001"
        };

        Assert.Null(product.ProductTypeId);
        Assert.Null(product.ProductClassId);
        Assert.Equal("RM:SKU-001", product.ExternalReference);
    }
}
