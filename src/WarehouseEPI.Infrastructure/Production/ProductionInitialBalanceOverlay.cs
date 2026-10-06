using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyBalanceService
{
    private async Task<ProductionDailyBalanceView> WithInitialBalancesAsync(ProductionDailyBalanceView balance,
        ProductionBalanceScenario? scenario, Guid[]? selectedProducts, CancellationToken token)
    {
        var totals = await db.ProductionInitialBalances.AsNoTracking().Where(x => x.WeekId == balance.WeekId)
            .Where(x => selectedProducts == null || selectedProducts.Contains(x.ProductId)).ToListAsync(token);
        foreach (var change in scenario?.InitialBalances ?? [])
        {
            totals.RemoveAll(x => x.ProductId == change.ProductId && x.Area == change.Area);
            totals.Add(new() { WeekId = balance.WeekId, ProductId = change.ProductId, Area = change.Area, Quantity = change.Quantity });
        }
        if (totals.Count == 0) return balance;
        var rows = balance.Rows.ToList();
        var missingIds = totals.Select(x => x.ProductId).Except(rows.Select(x => x.ProductId)).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(x => missingIds.Contains(x.Id)).ToListAsync(token);
        foreach (var product in products)
            foreach (var day in ProductionWeekCalendar.Days(balance.WeekStart))
            {
                ProductionDailyAreaBalance Empty(ProductionDailyArea area) => new(area, false, 0, 0, 0, 0, 0, 0, 0);
                rows.Add(new(day, product.Id, product.Sku, product.Description, 0, 0, 0, 0, 0, [],
                    Empty(ProductionDailyArea.Cutting), Empty(ProductionDailyArea.Sewing), Empty(ProductionDailyArea.ReadyToPack), 0));
            }
        foreach (var group in totals.GroupBy(x => x.ProductId))
        {
            ProductionDailyAreaBalance Select(ProductionDailyBalanceRow row, ProductionDailyArea area) => area switch
            { ProductionDailyArea.Cutting => row.Cutting, ProductionDailyArea.Sewing => row.Sewing, _ => row.ReadyToPack };
            var quantities = group.ToDictionary(x => x.Area, x => x.Quantity);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.ProductId != group.Key) continue;
                ProductionDailyAreaBalance Adjust(ProductionDailyAreaBalance area)
                {
                    if (!quantities.TryGetValue(area.Area, out var quantity)) return area;
                    var programmed = rows.Where(x => x.ProductId == row.ProductId && x.Date <= row.Date).Sum(x => Select(x, area.Area).ProgrammedToday);
                    var signed = quantity + programmed - area.Completed;
                    var future = rows.Where(x => x.ProductId == row.ProductId && x.Date > row.Date).Sum(x => Select(x, area.Area).ProgrammedToday);
                    var advance = Math.Min(Math.Max(0, -signed), future);
                    return area with
                    {
                        Applies = area.Applies || quantity > 0,
                        Opening = quantity,
                        NetPending = signed,
                        Pending = Math.Max(0, signed),
                        Advance = advance,
                        Extra = Math.Max(0, -signed) - advance,
                        OpeningToday = row.Date == balance.WeekStart ? quantity : 0
                    };
                }
                var cutting = Adjust(row.Cutting); var sewing = Adjust(row.Sewing); var pack = Adjust(row.ReadyToPack);
                var last = new[] { cutting, sewing, pack }.LastOrDefault(x => x.Applies);
                var final = last is null ? null : Select(rows.Single(x => x.ProductId == row.ProductId && x.Date == balance.WeekEnd), last.Area);
                var target = final is null ? 0 : quantities.TryGetValue(final.Area, out var initial)
                    ? initial + rows.Where(x => x.ProductId == row.ProductId).Sum(x => Select(x, final.Area).ProgrammedToday)
                    : final.SignedPending + final.Completed;
                rows[i] = row with
                {
                    Cutting = cutting,
                    Sewing = sewing,
                    ReadyToPack = pack,
                    ProgressPercent = target > 0 ? Math.Min(100, last!.Completed / target * 100) : last?.Completed > 0 ? 100 : 0
                };
            }
        }
        return balance with { Rows = rows.OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ProductId).ThenBy(x => x.Date).ToArray() };
    }
}
