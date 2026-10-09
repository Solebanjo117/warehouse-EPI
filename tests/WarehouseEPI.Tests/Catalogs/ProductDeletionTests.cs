using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Catalogs;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Security;

namespace WarehouseEPI.Tests.Catalogs;

public sealed class ProductDeletionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unused_product_can_be_deleted_with_catalog_settings(bool active)
    {
        await using var db = CreateContext();
        var product = new Product { Sku = "UNUSED", BaseUnitId = 1, IsActive = active };
        var other = new Product { Sku = "KEEP", BaseUnitId = 1 };
        var location = new Location { Code = "A-1", Kind = LocationKind.Area };
        db.AddRange(product, other, location);
        db.ProductBarcodes.Add(new ProductBarcode { Product = product, Barcode = "UNUSED" });
        db.ProductLocationAssignments.Add(new ProductLocationAssignment { Product = product, Location = location });
        await db.SaveChangesAsync();
        var service = new ProductDeletionService(db);

        Assert.False(await service.HasUsageAsync(product.Id, default));
        Assert.Equal(ProductDeletionResult.Deleted, await service.DeleteAsync(product.Id, default));
        Assert.Equal(other.Id, (await db.Products.SingleAsync()).Id);
        Assert.Empty(await db.ProductBarcodes.ToListAsync());
        Assert.Empty(await db.ProductLocationAssignments.ToListAsync());
        Assert.Single(await db.Locations.ToListAsync());
        Assert.Equal(ProductDeletionResult.NotFound, await service.DeleteAsync(product.Id, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-5)]
    public async Task Balance_records_block_deletion_even_at_zero(decimal quantity)
    {
        await using var db = CreateContext();
        var product = new Product { Sku = "USED", BaseUnitId = 1 };
        db.Add(product);
        await db.SaveChangesAsync();
        var service = new ProductDeletionService(db);
        Assert.False(await service.HasUsageAsync(product.Id, default));
        // Usage arriving after the confirmation GET must be checked again at POST.
        db.InventoryBalances.Add(new InventoryBalance { ProductId = product.Id, LocationId = Guid.NewGuid(), Quantity = quantity });
        await db.SaveChangesAsync();
        Assert.Equal(ProductDeletionResult.InUse, await service.DeleteAsync(product.Id, default));
        Assert.Single(await db.Products.ToListAsync());
    }

    [Fact]
    public async Task Every_mapped_operational_relationship_blocks_deletion()
    {
        await using var metadata = CreateContext();
        var references = metadata.Model.FindEntityType(typeof(Product))!.GetReferencingForeignKeys()
            .Where(fk => fk.DeclaringEntityType.ClrType != typeof(ProductBarcode)
                && fk.DeclaringEntityType.ClrType != typeof(ProductLocationAssignment)).ToList();
        Assert.NotEmpty(references);
        foreach (var reference in references)
        {
            await using var db = CreateContext();
            var product = new Product { Sku = "USED", BaseUnitId = 1 };
            db.Add(product);
            var dependent = Activator.CreateInstance(reference.DeclaringEntityType.ClrType)!;
            db.Add(dependent).Property(reference.Properties.Single().Name).CurrentValue = product.Id;
            await db.SaveChangesAsync();
            var service = new ProductDeletionService(db);
            Assert.True(await service.HasUsageAsync(product.Id, default), reference.DeclaringEntityType.Name);
            Assert.Equal(ProductDeletionResult.InUse, await service.DeleteAsync(product.Id, default));
            Assert.Single(await db.Products.ToListAsync());
        }
    }

    [Fact]
    public void Deletion_is_admin_only()
    {
        var policy = PageAccess.PolicyFor("/Admin/Catalogs/Products/Delete");
        Assert.Equal(PageAccess.AdminOnly, policy);
        Assert.False(PageAccess.Allows(policy, null));
        Assert.False(PageAccess.Allows(policy, "OPERATOR"));
        Assert.True(PageAccess.Allows(policy, "ADMIN"));
    }

    private static WarehouseDbContext CreateContext() => new(new DbContextOptionsBuilder<WarehouseDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString(), options => options.EnableNullChecks(false)).Options);
}
