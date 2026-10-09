using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Catalogs;

public sealed class ProductDeletionService(WarehouseDbContext db)
{
    private static readonly MethodInfo ReferenceQuery = typeof(ProductDeletionService)
        .GetMethod(nameof(HasReferenceAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public async Task<bool> HasUsageAsync(Guid id, CancellationToken token)
    {
        // Fail closed for every mapped product relationship, including future modules.
        // Barcodes and location assignments are catalog settings, not operational history.
        var productType = db.Model.FindEntityType(typeof(Product))!;
        foreach (var foreignKey in productType.GetReferencingForeignKeys())
        {
            var type = foreignKey.DeclaringEntityType.ClrType;
            if (type == typeof(ProductBarcode) || type == typeof(ProductLocationAssignment)) continue;
            if (foreignKey.Properties.Count != 1) return true;
            var property = foreignKey.Properties[0];
            if (property.ClrType != typeof(Guid) && property.ClrType != typeof(Guid?)) return true;
            var query = (Task<bool>)ReferenceQuery.MakeGenericMethod(type)
                .Invoke(this, [property.Name, id, token])!;
            if (await query) return true;
        }
        return false;
    }

    private Task<bool> HasReferenceAsync<TEntity>(string property, Guid id, CancellationToken token)
        where TEntity : class => db.Set<TEntity>().IgnoreQueryFilters()
            .AnyAsync(item => EF.Property<Guid?>(item, property) == id, token);

    public async Task<ProductDeletionResult> DeleteAsync(Guid id, CancellationToken token)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        try
        {
            // A product row lock prevents new FK references during the final validation.
            var product = db.Database.IsNpgsql()
                ? await db.Products.FromSqlInterpolated($"SELECT * FROM products WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(token)
                : await db.Products.SingleOrDefaultAsync(item => item.Id == id, token);
            if (product is null) return ProductDeletionResult.NotFound;
            if (await HasUsageAsync(id, token)) return ProductDeletionResult.InUse;
            db.ProductBarcodes.RemoveRange(await db.ProductBarcodes.Where(item => item.ProductId == id).ToListAsync(token));
            db.ProductLocationAssignments.RemoveRange(await db.ProductLocationAssignments.Where(item => item.ProductId == id).ToListAsync(token));
            db.Products.Remove(product);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return ProductDeletionResult.Deleted;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ProductDeletionResult.Changed;
        }
        catch (Exception exception) when (exception is PostgresException
            { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.ForeignKeyViolation }
            || exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.ForeignKeyViolation })
        {
            return ProductDeletionResult.Changed;
        }
    }
}

public enum ProductDeletionResult { Deleted, NotFound, InUse, Changed }
