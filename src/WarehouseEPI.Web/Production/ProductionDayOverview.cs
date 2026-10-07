using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Production;

public sealed record ProductionOverviewProduct(Guid Id, string? Description, short UnitId, string Unit);
public sealed record ProductionOverviewQuantity(string Unit, string? Sku, decimal Produced,
    decimal Shift1, decimal Shift2, decimal? Target, decimal Pending, decimal Opening);
public sealed record ProductionOverviewArea(ProductionDailyArea Area, IReadOnlyList<ProductionOverviewQuantity> Quantities);
public sealed record ProductionOverviewAttention(Guid ProductId, string Sku, string? Description,
    ProductionDailyArea Area, string Unit, decimal Pending, string Reason);
public sealed record ProductionDayOverview(IReadOnlyList<ProductionOverviewArea> Areas,
    IReadOnlyList<ProductionOverviewAttention> Attention)
{
    public int AttentionProductCount => Attention.Select(x => x.ProductId).Distinct().Count();

    public static ProductionDayOverview Create(ProductionDailySummary summary,
        IReadOnlyDictionary<Guid, ProductionOverviewProduct> metadata, int shift)
    {
        var attention = new List<ProductionOverviewAttention>();
        var areas = new List<ProductionOverviewArea>();
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
        {
            var rows = summary.Products.Select(product => (Product: product, Meta: metadata[product.ProductId],
                Balance: area switch
                {
                    ProductionDailyArea.Cutting => product.Cutting,
                    ProductionDailyArea.Sewing => product.Sewing,
                    _ => product.ReadyToPack
                }))
                .Where(x => x.Balance.Completed != 0 || x.Balance.SignedPending != 0 || x.Balance.Opening != 0 ||
                    x.Balance.ProgrammedToday != 0 || x.Balance.ToReconcile != 0).ToArray();
            // Unknown units are never combined, even with another unknown product.
            var quantities = rows.GroupBy(x => (x.Meta.UnitId, Separate: x.Meta.Unit == "UNASSIGNED" ? x.Product.ProductId : Guid.Empty))
                .OrderBy(x => x.First().Meta.Unit, StringComparer.Ordinal).ThenBy(x => x.First().Product.Sku, StringComparer.Ordinal)
                .Select(group => new ProductionOverviewQuantity(group.First().Meta.Unit,
                    group.Key.Separate == Guid.Empty ? null : group.First().Product.Sku,
                    group.Sum(x => shift == 1 ? x.Balance.CompletedShift1 : shift == 2 ? x.Balance.CompletedShift2 : x.Balance.Completed),
                    group.Sum(x => x.Balance.CompletedShift1), group.Sum(x => x.Balance.CompletedShift2),
                    group.All(x => x.Balance.DailyCoverage.Target > 0) ? group.Sum(x => x.Balance.DailyCoverage.Target) : null,
                    group.Sum(x => Math.Max(0, x.Balance.SignedPending)),
                    group.Sum(x => Math.Max(0, x.Balance.Opening)))).ToArray();
            areas.Add(new(area, quantities));
            foreach (var row in rows)
            {
                var reason = row.Balance.ToReconcile > 0 ? "Producción por conciliar"
                    : row.Balance.SignedPending > 0 ? "Pendiente del día"
                    : row.Meta.Unit == "UNASSIGNED" ? "Unidad sin asignar" : null;
                if (reason is not null) attention.Add(new(row.Product.ProductId, row.Product.Sku,
                    row.Meta.Description, area, row.Meta.Unit, Math.Max(0, row.Balance.SignedPending), reason));
            }
        }
        return new(areas, attention.OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ProductId).ThenBy(x => x.Area).ToArray());
    }
}
