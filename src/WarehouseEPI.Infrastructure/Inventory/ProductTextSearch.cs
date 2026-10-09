using System.Linq.Expressions;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Inventory;

public static class ProductTextSearch
{
    // Keep each caller's code/reference/barcode rules and extend only description matching.
    // Build scalar Contains expressions so filtering stays in SQL before pagination.
    public static IQueryable<Product> WhereProductText(
        this IQueryable<Product> query, string search, Expression<Func<Product, bool>> existingMatch)
    {
        var terms = search.ToUpperInvariant().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Length == 0)
            return query.Where(existingMatch);

        var parameter = existingMatch.Parameters[0];
        var description = Expression.Property(parameter, nameof(Product.Description));
        Expression match = Expression.NotEqual(description, Expression.Constant(null, typeof(string)));
        var upper = Expression.Call(description, nameof(string.ToUpper), Type.EmptyTypes);
        foreach (var term in terms)
        {
            var contains = Expression.Call(upper, nameof(string.Contains), Type.EmptyTypes, Expression.Constant(term));
            match = Expression.AndAlso(match, contains);
        }
        return query.Where(Expression.Lambda<Func<Product, bool>>(
            Expression.OrElse(existingMatch.Body, match), parameter));
    }
}
