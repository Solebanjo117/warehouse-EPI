using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Tests.Inventory;

[Collection(PostgreSqlInventoryCollection.CollectionName)]
public sealed class ProductTextSearchPostgreSqlTests(PostgreSqlInventoryFixture fixture)
{
    [Fact]
    public async Task Description_terms_execute_on_postgresql_before_result_limit()
    {
        await fixture.WithIsolatedDatabaseAsync(async isolated =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var seed = await isolated.SeedAsync($"SEARCH-{suffix}", $"S-{suffix}", "5197");
            await using var db = isolated.CreateDbContext();
            var product = await db.Products.SingleAsync(p => p.Id == seed.ProductId);
            product.Description = $"Bolsa de polietileno transparente {suffix} 50%";
            await db.SaveChangesAsync();
            var service = new OperationalInventoryQueryService(db);

            Assert.Equal(product.Id, Assert.Single(await service.SearchProductsAsync($"{suffix} transparente bolsa")).Id);
            Assert.Equal(product.Id, Assert.Single((await service.SearchInventoryAsync($"50% {suffix} bolsa")).Products).Id);
            Assert.Empty(await service.SearchProductsAsync($"{suffix} azul bolsa"));
            Assert.Empty(await service.SearchProductsAsync($"{suffix} 50_"));
        });
    }
}
