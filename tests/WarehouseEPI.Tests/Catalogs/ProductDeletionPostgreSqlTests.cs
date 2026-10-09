using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Catalogs;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Tests.Catalogs;

public sealed class ProductDeletionPostgreSqlTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Real_deletion_preserves_usage_and_handles_concurrent_deletes()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var source = Environment.GetEnvironmentVariable("WAREHOUSE_EPI_TEST_CONNECTION") ?? config.GetConnectionString("Warehouse")
            ?? throw new InvalidOperationException("Configure PostgreSQL test credentials.");
        var builder = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        var database = "warehouse_epi_product_delete_test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            builder.Database = database;
            WarehouseDbContext Open() => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseNpgsql(builder.ConnectionString).Options);
            await using var db = Open();
            await db.Database.MigrateAsync();
            var unused = new Product { Sku = "UNUSED", BaseUnitId = 1 };
            var used = new Product { Sku = "USED", BaseUnitId = 1 };
            var location = new Location { Code = "DELETE-TEST", Kind = LocationKind.Area };
            db.AddRange(unused, used, location);
            db.ProductBarcodes.Add(new ProductBarcode { Product = unused, Barcode = "UNUSED" });
            db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = unused, Location = location });
            db.InventoryBalances.Add(new InventoryBalance { Product = used, Location = location, Quantity = 0 });
            await db.SaveChangesAsync();
            var expectedLocationIds = (await db.Locations.Select(item => item.Id).ToArrayAsync())
                .OrderBy(id => id).ToArray();

            async Task<ProductDeletionResult> Delete(Guid id)
            {
                await using var context = Open();
                return await new ProductDeletionService(context).DeleteAsync(id, default);
            }

            Assert.Equal(ProductDeletionResult.InUse, await Delete(used.Id));
            var results = await Task.WhenAll(Delete(unused.Id), Delete(unused.Id));
            Assert.Single(results, result => result == ProductDeletionResult.Deleted);
            Assert.All(results, result => Assert.Contains(result, new[]
                { ProductDeletionResult.Deleted, ProductDeletionResult.NotFound, ProductDeletionResult.Changed }));
            await using var verify = Open();
            Assert.Equal(used.Id, (await verify.Products.SingleAsync()).Id);
            Assert.Single(await verify.InventoryBalances.ToListAsync());
            Assert.Equal(expectedLocationIds, (await verify.Locations.Select(item => item.Id).ToArrayAsync())
                .OrderBy(id => id).ToArray());
            Assert.Empty(await verify.ProductBarcodes.ToListAsync());
            Assert.Empty(await verify.ProductLocationAssignments.ToListAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
