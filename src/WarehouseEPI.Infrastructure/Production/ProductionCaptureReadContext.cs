using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyCaptureService
{
    // Local to one read-only validation phase. Never retained on the service or passed across a write.
    private sealed record CaptureReadContext(ProductionDailyConfiguration Configuration,
        HashSet<Guid> ActiveStages, HashSet<Guid> ActiveShifts, Dictionary<Guid, Product> Products,
        ProductionScheduleWeek? Week, IReadOnlyList<string> OpeningErrors)
    {
        public Dictionary<(DateOnly Date, ProductionDailyArea Area, Guid Product), ProductionAvailableProduct?> Availability { get; } = [];
        public Dictionary<Guid, ProductionDailyBalanceView?> Pending { get; } = [];
    }

    private async Task<CaptureReadContext> ReadCaptureContextAsync(DateOnly date, Guid[] productIds,
        CancellationToken token, ProductionDailyConfiguration? configuration = null)
    {
        using var measurement = ProductionBalanceDiagnostics.Source.StartActivity("capture.metadata");
        configuration ??= await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var stageIds = new[] { configuration.CuttingStageId, configuration.SewingStageId, configuration.ReadyToPackStageId };
        var stages = await db.ProductionStages.AsNoTracking().Where(x => x.IsActive && stageIds.Contains(x.Id)).Select(x => x.Id).ToArrayAsync(token);
        var shifts = await db.ProductionShifts.AsNoTracking().Where(x => x.IsActive &&
            (x.Id == configuration.Shift1Id || x.Id == configuration.Shift2Id)).Select(x => x.Id).ToArrayAsync(token);
        var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit)
            .Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Status == ProductionScheduleWeekStatus.Open && x.WeekStart <= date && x.WeekEnd >= date, token);
        var openingErrors = week?.ExplicitCarryover == true
            ? await new ProductionWeekOpeningService(db).RevalidateAsync(week.Id, token) : [];
        return new(configuration, stages.ToHashSet(), shifts.ToHashSet(), products, week, openingErrors);
    }
}
