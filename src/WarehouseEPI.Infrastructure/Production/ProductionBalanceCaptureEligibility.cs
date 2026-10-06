using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyBalanceService
{
    // This editor accepts production in every configured area, regardless of route or report activity.
    // This does not create stock, a production target, or an opening in any area.
    public async Task<IReadOnlySet<(Guid ProductId, ProductionDailyArea Area)>> GetCaptureEligibilityAsync(
        Guid weekId, DateOnly date, IReadOnlyCollection<Guid> selected, CancellationToken token = default)
    {
        var eligible = new HashSet<(Guid ProductId, ProductionDailyArea Area)>();
        if (selected.Count == 0 || !await db.ProductionScheduleWeeks.AsNoTracking().AnyAsync(x =>
            x.Id == weekId && x.Status == ProductionScheduleWeekStatus.Open &&
            x.WeekStart <= date && x.WeekEnd >= date, token)) return eligible;
        var ids = selected.ToArray();
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsActive)
            .Select(x => x.Id).ToArrayAsync(token);
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var activeStages = (await db.ProductionStages.AsNoTracking().Where(x => x.IsActive)
            .Select(x => x.Id).ToArrayAsync(token)).ToHashSet();
        foreach (var productId in products)
        {
            foreach (var area in Enum.GetValues<ProductionDailyArea>())
                if (ProductionDailyProcessFlow.Stage(config, area) is Guid stage && activeStages.Contains(stage))
                    eligible.Add((productId, area));
        }
        return eligible;
    }
}
