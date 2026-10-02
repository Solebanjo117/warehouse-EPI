using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionOpeningChange(Guid SourceWeekId, Guid SourceLineId, ProductionDailyArea Area,
    decimal Quantity, string ExpectedFingerprint);
public sealed record ProductionOpeningOption(Guid SourceWeekId, DateOnly SourceStart, DateOnly OriginalStart, Guid SourceLineId,
    Guid ProductId, string Sku, string Unit, bool AllowsDecimals, int Sequence, ProductionDailyArea Area,
    decimal Available, decimal Selected, bool Provisional, string Fingerprint, bool NeedsReview = false,
    string? OrderReference1 = null, string? OrderReference2 = null, string? OrderReference3 = null, string? Notes = null);

public sealed class ProductionWeekOpeningService(WarehouseDbContext db)
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    public async Task<IReadOnlyList<ProductionOpeningOption>> OptionsAsync(Guid weekId, CancellationToken token = default)
    {
        var target = await db.ProductionScheduleWeeks.AsNoTracking().SingleAsync(x => x.Id == weekId, token);
        return await OptionsCoreAsync(target.WeekStart, weekId, token);
    }

    public Task<IReadOnlyList<ProductionOpeningOption>> OptionsAsync(DateOnly weekStart, CancellationToken token = default) =>
        OptionsCoreAsync(weekStart, null, token);

    private async Task<IReadOnlyList<ProductionOpeningOption>> OptionsCoreAsync(DateOnly weekStart, Guid? weekId, CancellationToken token)
    {
        var weeks = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => x.WeekStart < weekStart && x.Status != ProductionScheduleWeekStatus.Draft).ToDictionaryAsync(x => x.Id, token);
        var weekIds = weeks.Keys.ToArray();
        var lines = await db.ProductionScheduleLines.AsNoTracking().Include(x => x.Product).ThenInclude(x => x.BaseUnit)
            .Include(x => x.WorkOrder).ThenInclude(x => x!.Stages)
            .Where(x => weekIds.Contains(x.WeekId) && !x.IsExtra)
            .OrderBy(x => x.PlannedDate).ThenBy(x => x.Sequence).ThenBy(x => x.Id).ToListAsync(token);
        var lineIds = lines.Select(x => x.Id).ToArray();
        var openings = await db.ProductionWeekOpenings.AsNoTracking().Where(x => lineIds.Contains(x.SourceLineId)).ToListAsync(token);
        var allocations = await db.ProductionDailyCaptureAllocations.AsNoTracking()
            .Where(x => lineIds.Contains(x.ScheduleLineId) && x.Capture.Status == ProductionDailyCaptureStatus.Active)
            .Select(x => new { x.ScheduleLineId, x.Capture.WeekId, x.Capture.Area, x.Quantity, x.Capture.EffectiveDate, x.Capture.Week.ExplicitCarryover }).ToListAsync(token);
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var productIds = lines.Select(x => x.ProductId).Distinct().ToArray();
        var routes = await db.ProductionRoutes.AsNoTracking().Include(x => x.Stages)
            .Where(x => productIds.Contains(x.ProductId) && x.IsActive).ToDictionaryAsync(x => x.ProductId, token);
        var legacyBudgets = new Dictionary<(Guid, Guid, ProductionDailyArea), decimal>();
        var legacyInherited = new Dictionary<(Guid, Guid, ProductionDailyArea), decimal>();
        foreach (var legacy in weeks.Values.Where(x => !x.ExplicitCarryover))
        {
            // Admission measures work owed by the selected week, not a receipt of material.
            // Its historical opening remains the reported opening; never borrow another week's plan.
            var balance = await new ProductionDailyBalanceService(db).GetAsync(legacy.Id, token: token);
            foreach (var row in balance!.Rows.Where(x => x.Date == legacy.WeekEnd))
                foreach (var area in new[] { row.Cutting, row.Sewing, row.ReadyToPack })
                {
                    legacyBudgets[(legacy.Id, row.ProductId, area.Area)] = area.Pending;
                    legacyInherited[(legacy.Id, row.ProductId, area.Area)] = Math.Max(0, area.Opening -
                        lines.Where(x => x.WeekId == legacy.Id && !x.IsCancelled && x.IsCarryover &&
                            (x.StartArea ?? ProductionDailyArea.Cutting) == area.Area).Sum(x => x.Quantity));
                }
        }
        var residuals = await db.ProductionDailyCaptures.AsNoTracking()
            .Where(x => weekIds.Contains(x.WeekId) && x.Status == ProductionDailyCaptureStatus.Active)
            .Select(x => new { x.WeekId, x.ProductId, x.Area, Quantity = x.Quantity - x.Allocations.Where(a => !a.ScheduleLine.IsExtra).Sum(a => a.Quantity) }).ToListAsync(token);
        var residualByArea = residuals.GroupBy(x => (x.WeekId, x.ProductId, x.Area)).ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        bool Applies(ProductionScheduleLine line, ProductionDailyArea area)
        {
            var stages = line.WorkOrder?.Stages.OrderBy(x => x.Sequence).Select(x => x.SourceStageId)
                ?? routes.GetValueOrDefault(line.ProductId)?.Stages.OrderBy(x => x.Sequence).Select(x => x.StageId) ?? [];
            return ProductionDailyProcessFlow.Resolve(config, stages, line.IsCarryover ? line.StartArea : null).Contains(area);
        }
        // Historical captures can lack allocation rows. Attribute their residual once to roots,
        // then reuse that attribution for every week in the chain, including its ancestors.
        var legacyAdmissions = new Dictionary<(Guid WeekId, Guid LineId, ProductionDailyArea Area), (decimal Admitted, decimal ConsumedBefore)>();
        var legacyResidualRoots = new Dictionary<(Guid WeekId, Guid LineId, ProductionDailyArea Area), decimal>();
        foreach (var source in weeks.Values.OrderBy(x => x.WeekStart))
        foreach (var line in lines)
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
        {
            if (!Applies(line, area)) continue;
            var own = line.WeekId == source.Id && !line.IsCancelled &&
                (source.ExplicitCarryover || !line.IsCarryover || (line.StartArea ?? ProductionDailyArea.Cutting) == area) ? line.Quantity : 0;
            var admitted = openings.Where(x => x.WeekId == source.Id && x.SourceLineId == line.Id && x.Area == area).Sum(x => x.Quantity);
            var consumedBefore = 0m;
            if (!source.ExplicitCarryover && line.WorkOrderId.HasValue && weeks[line.WeekId].WeekStart < source.WeekStart && !line.IsCancelled)
            {
                consumedBefore = allocations.Where(x => x.ScheduleLineId == line.Id && x.Area == area && (!x.ExplicitCarryover || x.WeekId == line.WeekId) &&
                    x.EffectiveDate < source.WeekStart).Sum(x => x.Quantity) + legacyResidualRoots
                    .Where(x => x.Key.LineId == line.Id && x.Key.Area == area &&
                        (!weeks[x.Key.WeekId].ExplicitCarryover || x.Key.WeekId == line.WeekId) &&
                        weeks[x.Key.WeekId].WeekStart < source.WeekStart).Sum(x => x.Value);
                var inheritedKey = (source.Id, line.ProductId, area);
                var inherited = Math.Min(Math.Max(0, line.Quantity - consumedBefore), legacyInherited.GetValueOrDefault(inheritedKey));
                admitted += inherited;
                legacyInherited[inheritedKey] = legacyInherited.GetValueOrDefault(inheritedKey) - inherited;
            }
            if (!source.ExplicitCarryover) legacyAdmissions[(source.Id, line.Id, area)] = (admitted, consumedBefore);
            var consumedHere = allocations.Where(x => x.ScheduleLineId == line.Id && x.Area == area && x.WeekId == source.Id).Sum(x => x.Quantity);
            var residualKey = (source.Id, line.ProductId, area);
            var residualUsed = Math.Min(Math.Max(0, own + admitted - consumedHere), residualByArea.GetValueOrDefault(residualKey));
            legacyResidualRoots[(source.Id, line.Id, area)] = residualUsed;
            residualByArea[residualKey] = residualByArea.GetValueOrDefault(residualKey) - residualUsed;
        }
        var result = new List<ProductionOpeningOption>();
        foreach (var source in weeks.Values.OrderBy(x => x.WeekStart))
        foreach (var line in lines)
        foreach (var area in Enum.GetValues<ProductionDailyArea>())
        {
            if (!Applies(line, area)) continue;
            var own = line.WeekId == source.Id && !line.IsCancelled ? line.Quantity : 0;
            if (!source.ExplicitCarryover && line.IsCarryover && (line.StartArea ?? ProductionDailyArea.Cutting) != area)
                own = 0;
            var admitted = openings.Where(x => x.WeekId == source.Id && x.SourceLineId == line.Id && x.Area == area).Sum(x => x.Quantity);
            var consumedBefore = 0m;
            if (!source.ExplicitCarryover)
            {
                (admitted, consumedBefore) = legacyAdmissions.GetValueOrDefault((source.Id, line.Id, area));
            }
            var selected = openings.Where(x => x.WeekId == weekId && x.SourceWeekId == source.Id && x.SourceLineId == line.Id && x.Area == area).Sum(x => x.Quantity);
            if (own + admitted == 0 && selected == 0) continue;
            var consumed = allocations.Where(x => x.ScheduleLineId == line.Id && x.Area == area &&
                (source.ExplicitCarryover ? x.WeekId == source.Id : !x.ExplicitCarryover || x.WeekId == line.WeekId)).Sum(x => x.Quantity) - consumedBefore;
            consumed += legacyResidualRoots.Where(x => x.Key.LineId == line.Id && x.Key.Area == area &&
                (source.ExplicitCarryover ? x.Key.WeekId == source.Id : !weeks[x.Key.WeekId].ExplicitCarryover || x.Key.WeekId == line.WeekId)).Sum(x => x.Value);
            if (source.ExplicitCarryover && own > 0)
            {
                consumed += allocations.Where(x => x.ScheduleLineId == line.Id && x.Area == area && !x.ExplicitCarryover).Sum(x => x.Quantity)
                    + legacyResidualRoots.Where(x => x.Key.LineId == line.Id && x.Key.Area == area && !weeks[x.Key.WeekId].ExplicitCarryover).Sum(x => x.Value);
            }
            var outgoing = openings.Where(x => x.SourceLineId == line.Id && x.Area == area &&
                (source.ExplicitCarryover ? x.SourceWeekId == source.Id && x.WeekId != weekId ||
                    own > 0 && weeks.GetValueOrDefault(x.SourceWeekId)?.ExplicitCarryover == false :
                    (weeks.GetValueOrDefault(x.SourceWeekId)?.ExplicitCarryover == false || x.SourceWeekId == line.WeekId) &&
                    (x.WeekId != weekId || x.SourceWeekId != source.Id))).Sum(x => x.Quantity);
            var residualKey = (source.Id, line.ProductId, area);
            var remaining = Math.Max(0, own + admitted - consumed);
            if (!source.ExplicitCarryover)
            {
                remaining = Math.Min(remaining, legacyBudgets.GetValueOrDefault(residualKey));
                legacyBudgets[residualKey] = legacyBudgets.GetValueOrDefault(residualKey) - remaining;
            }
            var available = Math.Max(0, remaining - outgoing);
            if (available == 0 && selected == 0) continue;
            var fingerprint = Hash(new { WeekVersion = source.Version, LineVersion = line.Version, own, admitted, consumed, outgoing, remaining,
                line.Product.IsActive, line.Product.BaseUnitId, line.Product.BaseUnit.AllowsDecimals });
            var needsReview = openings.Any(x => x.WeekId == weekId && x.SourceWeekId == source.Id && x.SourceLineId == line.Id && x.Area == area && x.Quantity > 0 && x.SourceFingerprint != fingerprint);
            result.Add(new(source.Id, source.WeekStart, weeks[line.WeekId].WeekStart, line.Id, line.ProductId, line.Product.Sku, line.Product.BaseUnit.Code,
                line.Product.BaseUnit.AllowsDecimals, line.Sequence, area, line.Product.IsActive && !line.IsCancelled ? available : 0, selected,
                source.Status != ProductionScheduleWeekStatus.Closed, fingerprint, needsReview,
                line.OrderReference1, line.OrderReference2, line.OrderReference3, line.Notes));
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(Guid weekId, IReadOnlyList<ProductionOpeningChange> changes, CancellationToken token = default)
    {
        var week = await db.ProductionScheduleWeeks.AsNoTracking().SingleAsync(x => x.Id == weekId, token);
        if (!week.ExplicitCarryover || week.Status == ProductionScheduleWeekStatus.Closed)
            return ["La semana no admite cambios de arrastre inicial."];
        return await ValidateCoreAsync(week.WeekStart, weekId, changes, token);
    }

    public Task<IReadOnlyList<string>> ValidateAsync(DateOnly weekStart, IReadOnlyList<ProductionOpeningChange> changes, CancellationToken token = default) =>
        ValidateCoreAsync(weekStart, null, changes, token);

    private async Task<IReadOnlyList<string>> ValidateCoreAsync(DateOnly weekStart, Guid? weekId, IReadOnlyList<ProductionOpeningChange> changes, CancellationToken token)
    {
        var errors = new List<string>();
        if (changes.Select(x => (x.SourceWeekId, x.SourceLineId, x.Area)).Distinct().Count() != changes.Count)
            return ["Hay arrastres repetidos en el grupo."];
        var options = await OptionsCoreAsync(weekStart, weekId, token);
        var current = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.WeekId == weekId).ToListAsync(token);
        foreach (var sourceId in changes.Where(x => x.Quantity > 0).Select(x => x.SourceWeekId).Distinct())
            errors.AddRange(await RevalidateAsync(sourceId, token));
        foreach (var change in changes)
        {
            var option = options.SingleOrDefault(x => x.SourceWeekId == change.SourceWeekId && x.SourceLineId == change.SourceLineId && x.Area == change.Area);
            if (option is null)
            { errors.Add($"El origen del arrastre · {change.Area} ya no está disponible. Prepara de nuevo la copia."); continue; }
            if (option.Fingerprint != change.ExpectedFingerprint || change.Quantity > option.Available ||
                change.Quantity < 0 || change.Quantity > 99999999999999.9999m || decimal.Round(change.Quantity, 4) != change.Quantity ||
                !option.AllowsDecimals && decimal.Truncate(change.Quantity) != change.Quantity)
            { errors.Add($"{option.Sku} · {change.Area}: el pendiente cambió o la cantidad no es válida. Revisa el origen."); continue; }
            var other = current.Where(x => x.SourceLineId == change.SourceLineId && x.Area == change.Area && x.SourceWeekId != change.SourceWeekId)
                .Where(x => !changes.Any(c => c.SourceWeekId == x.SourceWeekId && c.SourceLineId == x.SourceLineId && c.Area == x.Area)).Sum(x => x.Quantity)
                + changes.Where(x => x.SourceLineId == change.SourceLineId && x.Area == change.Area && x.SourceWeekId != change.SourceWeekId).Sum(x => x.Quantity);
            var consumed = await db.ProductionDailyCaptureAllocations.AsNoTracking().Where(x => x.ScheduleLineId == change.SourceLineId &&
                x.Capture.WeekId == weekId && x.Capture.Area == change.Area && x.Capture.Status == ProductionDailyCaptureStatus.Active).SumAsync(x => x.Quantity, token);
            var forwarded = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.SourceWeekId == weekId && x.SourceLineId == change.SourceLineId && x.Area == change.Area).SumAsync(x => x.Quantity, token);
            if (change.Quantity + other < consumed + forwarded)
            {
                errors.Add($"{option.Sku} · {change.Area}: hay {consumed} consumidas y {forwarded} comprometidas. Corrige primero las capturas o los arrastres posteriores.");
            }
        }
        var productAreas = options.Where(o => changes.Any(c => c.SourceWeekId == o.SourceWeekId && c.SourceLineId == o.SourceLineId && c.Area == o.Area))
            .Select(o => (o.ProductId, o.Area)).Distinct().ToArray();
        foreach (var (productId, area) in productAreas)
        {
            var before = current.Where(x => x.ProductId == productId && x.Area == area).Sum(x => x.Quantity);
            var delta = changes.Where(c => options.Any(o => o.ProductId == productId && o.Area == area && c.SourceWeekId == o.SourceWeekId && c.SourceLineId == o.SourceLineId && c.Area == o.Area))
                .Sum(c => c.Quantity - (current.SingleOrDefault(x => x.SourceWeekId == c.SourceWeekId && x.SourceLineId == c.SourceLineId && x.Area == c.Area)?.Quantity ?? 0));
            if (delta >= 0) continue;
            var own = weekId.HasValue ? await OwnQuantityAsync(weekId.Value, productId, area, token) : 0;
            var consumed = await db.ProductionDailyCaptures.Where(x => x.WeekId == weekId && x.ProductId == productId && x.Area == area && x.Status == ProductionDailyCaptureStatus.Active).SumAsync(x => x.Quantity, token);
            var forwarded = await db.ProductionWeekOpenings.Where(x => x.SourceWeekId == weekId && x.ProductId == productId && x.Area == area).SumAsync(x => x.Quantity, token);
            if (own + before + delta < Math.Min(own + before, consumed + forwarded))
                errors.Add($"{options.First(o => o.ProductId == productId).Sku} · {area}: corrige primero las capturas o los arrastres posteriores que comprometen este saldo.");
        }
        return errors;
    }

    private async Task<decimal> OwnQuantityAsync(Guid weekId, Guid productId, ProductionDailyArea area, CancellationToken token)
    {
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var lines = await db.ProductionScheduleLines.AsNoTracking().Include(x => x.WorkOrder).ThenInclude(x => x!.Stages)
            .Where(x => x.WeekId == weekId && x.ProductId == productId && !x.IsCancelled && !x.IsExtra).ToListAsync(token);
        return lines.Where(x => ProductionDailyProcessFlow.Resolve(config, x.WorkOrder?.Stages.Select(s => s.SourceStageId) ?? [], x.IsCarryover ? x.StartArea : null).Contains(area)).Sum(x => x.Quantity);
    }

    public async Task<string?> CaptureCommitmentErrorAsync(Guid weekId, Guid productId, ProductionDailyArea area, decimal quantity, CancellationToken token)
    {
        if (await db.ProductionInitialBalances.AnyAsync(x => x.WeekId == weekId && x.ProductId == productId && x.Area == area, token)) return null;
        var commitments = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.SourceWeekId == weekId && x.ProductId == productId && x.Area == area && x.Quantity > 0).ToListAsync(token);
        if (commitments.Count == 0) return null;
        var own = await OwnQuantityAsync(weekId, productId, area, token);
        var admitted = await db.ProductionWeekOpenings.Where(x => x.WeekId == weekId && x.ProductId == productId && x.Area == area).SumAsync(x => x.Quantity, token);
        var consumed = await db.ProductionDailyCaptures.Where(x => x.WeekId == weekId && x.ProductId == productId && x.Area == area && x.Status == ProductionDailyCaptureStatus.Active).SumAsync(x => x.Quantity, token);
        return consumed + quantity + commitments.Sum(x => x.Quantity) > own + admitted
            ? $"Esta captura usaría pendientes comprometidos en semanas posteriores. Revisa primero los arrastres: {string.Join(", ", commitments.Select(x => x.WeekId).Distinct())}." : null;
    }

    public async Task ApplyAsync(Guid weekId, IReadOnlyList<ProductionOpeningChange> changes, CancellationToken token)
    {
        foreach (var change in changes)
        {
            var row = await db.ProductionWeekOpenings.SingleOrDefaultAsync(x => x.WeekId == weekId && x.SourceWeekId == change.SourceWeekId && x.SourceLineId == change.SourceLineId && x.Area == change.Area, token);
            if (row is null)
            {
                var productId = await db.ProductionScheduleLines.Where(x => x.Id == change.SourceLineId).Select(x => x.ProductId).SingleAsync(token);
                row = new() { WeekId = weekId, SourceWeekId = change.SourceWeekId, SourceLineId = change.SourceLineId, ProductId = productId, Area = change.Area };
                db.ProductionWeekOpenings.Add(row);
            }
            row.Quantity = change.Quantity; row.SourceFingerprint = change.ExpectedFingerprint; row.Version++;
        }
    }

    public async Task<IReadOnlyList<string>> RevalidateAsync(Guid weekId, CancellationToken token = default)
    {
        var rows = await db.ProductionWeekOpenings.AsNoTracking().Where(x => x.WeekId == weekId && x.Quantity > 0 &&
            !db.ProductionInitialBalances.Any(b => b.WeekId == weekId && b.ProductId == x.ProductId && b.Area == x.Area)).ToListAsync(token);
        if (rows.Count == 0) return [];
        var options = await OptionsAsync(weekId, token);
        var productIds = rows.Select(x => x.ProductId).Distinct().ToArray();
        var skus = await db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Sku, token);
        var errors = rows.Where(row => !options.Any(x => x.SourceWeekId == row.SourceWeekId && x.SourceLineId == row.SourceLineId && x.Area == row.Area &&
            x.Available >= row.Quantity && x.Fingerprint == row.SourceFingerprint))
            .Select(row => $"El origen del arrastre {skus[row.ProductId]} · {row.Area} cambió. Revisa Arrastre inicial en Programa semanal.").ToList();
        if (errors.Count == 0)
            foreach (var sourceId in rows.Select(x => x.SourceWeekId).Distinct()) errors.AddRange(await RevalidateAsync(sourceId, token));
        return errors;
    }
}
