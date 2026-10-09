using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

namespace WarehouseEPI.Tests.Inventory;

public sealed class ProductTextSearchTests
{
    [Fact]
    public void Description_predicate_translates_to_postgresql_with_server_side_limit()
    {
        using var db = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=unused").Options);
        var sql = ProductPageSupport.ApplySearch(db.Products.Where(p => p.IsActive), "bolsa transparente")
            .OrderBy(p => p.Sku).Take(10).ToQueryString();
        Assert.Contains("LIKE '%BOLSA%'", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE '%TRANSPARENTE%'", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("transparente bolsa")]
    [InlineData(" bolsa\ttranspa  ")]
    [InlineData("bolsa bolsa")]
    [InlineData("50% bolsa")]
    public void Catalog_matches_all_description_fragments(string search)
    {
        var match = new Product { Sku = "MATCH", Description = "Bolsa de polietileno transparente 50%" };
        Product[] products = [match, new() { Sku = "NULL" }, new() { Sku = "MISS", Description = "Rollo azul" }];
        Assert.Equal(match, Assert.Single(ProductPageSupport.ApplySearch(products.AsQueryable(), search)));
    }

    [Fact]
    public void Original_predicate_is_preserved_and_terms_cannot_match_different_fields()
    {
        Product[] products = [
            new() { Sku = "BOLSA", Description = "Transparente" },
            new() { Sku = "CODE", ExternalReference = "REF WITH SPACES" }];
        Assert.Empty(ProductPageSupport.ApplySearch(products.AsQueryable(), "bolsa transparente"));
        Assert.Equal("CODE", Assert.Single(ProductPageSupport.ApplySearch(products.AsQueryable(), "ref with spaces")).Sku);
        Assert.Empty(products.AsQueryable().WhereProductText("   ", p => false));
    }
}
