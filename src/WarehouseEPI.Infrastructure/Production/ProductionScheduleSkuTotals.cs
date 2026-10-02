using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;

namespace WarehouseEPI.Infrastructure.Production;

public sealed partial class ProductionDailyScheduleService
{
    // Translate daily SKU totals without replacing historical lines or their physical allocations.
    private async Task<(IReadOnlyList<ProductionScheduleDraftChange> Changes, IReadOnlyList<string> Errors)>
        ExpandSkuTotalsAsync(ProductionScheduleWeek week, SaveProductionScheduleDraftCommand command, CancellationToken token)
    {
        if (command.SkuTotals is null) return (command.Changes, []);
        if (command.Changes.Count > 0 || command.SkuTotals.GroupBy(x => (x.ProductId, x.PlannedDate)).Any(x => x.Count() > 1) ||
            command.SkuTotals.GroupBy(x => x.ProductId).Any(group => group.Select(x =>
                (x.OrderReference1, x.OrderReference2, x.OrderReference3, x.Notes)).Distinct().Count() > 1))
            return ([], ["La preparación contiene totales repetidos o cambios incompatibles."]);
        var result = new List<ProductionScheduleDraftChange>();
        var errors = new List<string>();
        var ids = command.SkuTotals.Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit)
            .Where(x => ids.Contains(x.Id) && x.IsActive).ToDictionaryAsync(x => x.Id, token);
        var active = week.Lines.Where(x => !x.IsCancelled && !x.IsExtra && !x.IsCarryover && ids.Contains(x.ProductId))
            .OrderBy(x => x.Sequence).ThenBy(x => x.Id).ToArray();
        var eligibility = await GetDeletionEligibilityAsync(week.Id, active.Select(x => x.Id).ToArray(), token);
        var lineIds = active.Select(x => x.Id).ToArray();
        var allocations = await db.ProductionDailyCaptureAllocations.AsNoTracking()
            .Where(x => lineIds.Contains(x.ScheduleLineId) && x.Capture.Status == ProductionDailyCaptureStatus.Active)
            .Select(x => new { x.ScheduleLineId, x.Capture.Area, x.Quantity }).ToListAsync(token);
        var captured = allocations.GroupBy(x => x.ScheduleLineId).ToDictionary(group => group.Key,
            group => group.GroupBy(x => x.Area).Max(area => area.Sum(x => x.Quantity)));
        foreach (var total in command.SkuTotals)
        {
            if (!products.TryGetValue(total.ProductId, out var product) || total.Quantity < 0 ||
                !PreparedLineValid(total with { Quantity = total.Quantity == 0 ? 1 : total.Quantity }, week.WeekStart, week.WeekEnd, product.BaseUnit.AllowsDecimals))
            { errors.Add("Revisa SKU, día, cantidad y detalles de los productos preparados."); continue; }
            var lines = active.Where(x => x.ProductId == total.ProductId && x.PlannedDate == total.PlannedDate).ToArray();
            if (lines.Length == 0)
            {
                if (total.Quantity > 0) result.Add(new("add", null, null, total));
                continue;
            }
            var amounts = lines.Select(x => x.Quantity).ToArray();
            var floors = new decimal[lines.Length];
            for (var i = 0; i < lines.Length; i++)
            {
                var processed = lines[i].WorkOrderId is Guid order ? await EffectiveGoodAsync(order, token) : 0;
                processed = Math.Max(processed, captured.GetValueOrDefault(lines[i].Id));
                floors[i] = Math.Max(processed, eligibility.TryGetValue(lines[i].Id, out var allowed) && allowed.Allowed && !allowed.RequiresPin
                    ? 0 : product.BaseUnit.AllowsDecimals ? 0.0001m : 1m);
            }
            var minimum = floors.Sum();
            if (total.Quantity < minimum)
            {
                errors.Add($"{product.Sku}, {total.PlannedDate:yyyy-MM-dd}: el mínimo permitido es {minimum:0.####} {product.BaseUnit.Code}.");
                continue;
            }
            var difference = total.Quantity - amounts.Sum();
            if (difference > 0) amounts[0] += difference;
            else for (var i = amounts.Length - 1; i >= 0 && difference < 0; i--)
            {
                var reduction = Math.Min(Math.Max(0, amounts[i] - floors[i]), -difference);
                amounts[i] -= reduction; difference += reduction;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (amounts[i] == 0) result.Add(new("remove", line.Id, line.Version, null));
                else if (amounts[i] != line.Quantity || line.OrderReference1 != total.OrderReference1 ||
                    line.OrderReference2 != total.OrderReference2 || line.OrderReference3 != total.OrderReference3 || line.Notes != total.Notes)
                    result.Add(new("edit", line.Id, line.Version, total with { Quantity = amounts[i],
                        OriginalType = line.OriginalType, OriginalAnnotation1 = line.OriginalAnnotation1,
                        OriginalAnnotation2 = line.OriginalAnnotation2, OriginalAnnotation1Kind = line.OriginalAnnotation1Kind,
                        OriginalAnnotation2Kind = line.OriginalAnnotation2Kind }));
            }
        }
        return (result, errors);
    }
}
