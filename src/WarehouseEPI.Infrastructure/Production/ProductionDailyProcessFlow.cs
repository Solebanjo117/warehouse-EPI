using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

internal static class ProductionDailyProcessFlow
{
    internal static Guid? Stage(ProductionDailyConfiguration config, ProductionDailyArea area) => area switch
    {
        ProductionDailyArea.Cutting => config.CuttingStageId,
        ProductionDailyArea.Sewing => config.SewingStageId,
        _ => config.ReadyToPackStageId
    };

    internal static ProductionDailyArea[] Resolve(ProductionDailyConfiguration config,
        IEnumerable<Guid> routeStages, ProductionDailyArea? startArea)
    {
        var areas = Enum.GetValues<ProductionDailyArea>();
        var routeAreas = routeStages.SelectMany(stage => areas.Where(area => Stage(config, area) == stage)).ToArray();
        var chosen = routeAreas.Length > 0 && routeAreas.SequenceEqual(routeAreas.Distinct().Order()) ? routeAreas : areas;
        if (startArea is ProductionDailyArea start)
            chosen = chosen.Contains(start) ? chosen.Where(x => x >= start).ToArray() : areas.Where(x => x >= start).ToArray();
        return chosen;
    }
}
