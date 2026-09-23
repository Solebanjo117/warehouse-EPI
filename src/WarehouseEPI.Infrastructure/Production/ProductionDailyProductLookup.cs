using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionProductSuggestion(Guid Id, string Sku, string? Description);
public sealed record ProductionProductSuggestionGroup(int Group, int Offset, bool HasMore,
    IReadOnlyList<ProductionProductSuggestion> Items);

public sealed partial class ProductionDailyCaptureService
{
    public async Task<IReadOnlyList<ProductionProductSuggestionGroup>> SearchDailyProductsAsync(
        DateOnly date, ProductionDailyArea area, string? text, int? group = null, int offset = 0,
        CancellationToken token = default)
    {
        if (!Enum.IsDefined(area) || group is < 0 or > 2) return [];
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x => x.WeekStart == monday, token);
        if (week is null) return [];
        var planned = await db.ProductionScheduleLines.AsNoTracking()
            .Where(x => x.WeekId == week.Id && !x.IsCancelled && !x.IsExtra).Select(x => x.ProductId)
            .Union(db.ProductionCarryoverPlans.AsNoTracking().Where(x => x.WeekId == week.Id).Select(x => x.ProductId))
            .ToArrayAsync(token);
        var pending = (await GetAvailabilityAsync(date, area, priorOnly: true, token: token))
            .Select(x => x.ProductId).Except(planned).ToArray();
        var term = text?.Trim().ToUpperInvariant() ?? "";
        var result = new List<ProductionProductSuggestionGroup>();
        foreach (var category in group.HasValue ? new[] { group.Value } : new[] { 0, 1, 2 })
        {
            if (category == 2 && term.Length < 2) continue;
            var query = db.Products.AsNoTracking().Where(x => x.IsActive);
            query = category switch
            {
                0 => query.Where(x => planned.Contains(x.Id)),
                1 => query.Where(x => pending.Contains(x.Id)),
                _ => query.Where(x => !planned.Contains(x.Id) && !pending.Contains(x.Id))
            };
            if (term.Length > 0) query = query.Where(x => x.Sku.ToUpper().Contains(term) ||
                x.Description != null && x.Description.ToUpper().Contains(term));
            var start = Math.Clamp(offset, 0, 100_000);
            var matches = await query.OrderBy(x => x.Sku.ToUpper() == term ? 0 : x.Sku.ToUpper().StartsWith(term) ? 1 : 2)
                .ThenBy(x => x.Sku).ThenBy(x => x.Id).Skip(start).Take(11)
                .Select(x => new ProductionProductSuggestion(x.Id, x.Sku, x.Description)).ToListAsync(token);
            result.Add(new(category, start, matches.Count > 10, matches.Take(10).ToArray()));
        }
        return result;
    }
}
