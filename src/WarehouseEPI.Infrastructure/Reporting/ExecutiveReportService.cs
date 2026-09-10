using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>Consolida la situación actual y la actividad comparable del almacén.</summary>
public sealed class ExecutiveReportService(
    WarehouseDbContext dbContext,
    WarehouseSettingsService settingsService,
    InventoryAnalyticsService analyticsService,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ExecutiveReportDto> GetExecutiveReportAsync(
        ExecutiveReportFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var nowUtc = _timeProvider.GetUtcNow();
        var generatedAtLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var warehouseDate = DateOnly.FromDateTime(generatedAtLocal.DateTime);

        var fromUtc = filter.FromUtc ?? ToUtcStart(new DateOnly(warehouseDate.Year, warehouseDate.Month, 1), timeZone);
        var toUtc = filter.ToUtc ?? ToUtcStart(warehouseDate.AddDays(1), timeZone);
        var periodLength = toUtc - fromUtc;
        var previousFromUtc = filter.PreviousFromUtc ?? fromUtc - periodLength;
        var previousToUtc = filter.PreviousToUtc ?? fromUtc;
        var periodLabel = string.IsNullOrWhiteSpace(filter.PeriodLabel) ? "Período actual" : filter.PeriodLabel;
        var previousPeriodLabel = string.IsNullOrWhiteSpace(filter.PreviousPeriodLabel) ? "Período anterior" : filter.PreviousPeriodLabel;

        var totalActiveSkus = await dbContext.Products.AsNoTracking().CountAsync(product => product.IsActive, cancellationToken);
        var lowStockSkus = await (
            from product in dbContext.Products.AsNoTracking()
            where product.IsActive
            join balance in dbContext.InventoryBalances.AsNoTracking() on product.Id equals balance.ProductId into balances
            let total = balances.Sum(balance => (decimal?)balance.Quantity) ?? 0m
            where total < product.MinimumStock
            select product.Id).CountAsync(cancellationToken);

        var stagnantExport = await analyticsService.GetStagnantExportAsync(
            new InventoryAnalyticsFilter(StagnantCategory: StagnantCategory.Days90Plus), nowUtc,
            cancellationToken: cancellationToken);
        var coverageFromUtc = ToUtcStart(warehouseDate.AddDays(-29), timeZone);
        var coverageToUtc = ToUtcStart(warehouseDate.AddDays(1), timeZone);
        var coverageExport = await analyticsService.GetCoverageExportAsync(
            new InventoryAnalyticsFilter(
                FromUtc: coverageFromUtc, ToUtc: coverageToUtc,
                CoverageClassification: CoverageClassification.Critical),
            nowUtc, cancellationToken: cancellationToken);
        var health = new ExecutiveInventoryHealthDto(
            totalActiveSkus, lowStockSkus, stagnantExport.TotalRows, coverageExport.TotalRows);

        var rackStates = dbContext.Locations.AsNoTracking()
            .Where(location => location.Kind == LocationKind.Rack &&
                               location.IsActive && location.IsPhysicallyPresent)
            .Select(location => new
            {
                location.IsBlocked,
                HasNegative = dbContext.InventoryBalances.Any(balance =>
                    balance.LocationId == location.Id && balance.Quantity < 0m),
                HasPositive = dbContext.InventoryBalances.Any(balance =>
                    balance.LocationId == location.Id && balance.Quantity > 0m)
            });
        var capacityRaw = await rackStates.GroupBy(_ => 1).Select(group => new
        {
            Total = group.Count(),
            Blocked = group.Count(item => item.IsBlocked),
            Negative = group.Count(item => !item.IsBlocked && item.HasNegative),
            Occupied = group.Count(item => !item.IsBlocked && !item.HasNegative && item.HasPositive),
            Empty = group.Count(item => !item.IsBlocked && !item.HasNegative && !item.HasPositive)
        }).SingleOrDefaultAsync(cancellationToken);
        var blocked = capacityRaw?.Blocked ?? 0;
        var negative = capacityRaw?.Negative ?? 0;
        var occupied = capacityRaw?.Occupied ?? 0;
        var empty = capacityRaw?.Empty ?? 0;
        var usable = occupied + negative + empty;
        var capacity = new ExecutiveCapacityDto(
            capacityRaw?.Total ?? 0, occupied, empty,
            usable == 0 ? 0m : Math.Round(occupied * 100m / usable, 2), blocked, negative);

        var effectiveMovements = dbContext.InventoryMovements.AsNoTracking().WhereEffective(dbContext);
        var currentFlow = await AggregateFlowAsync(effectiveMovements, fromUtc, toUtc, cancellationToken);
        var previousFlow = await AggregateFlowAsync(effectiveMovements, previousFromUtc, previousToUtc, cancellationToken);
        var comparison = new ExecutiveOperationalComparisonDto(
            Compare(currentFlow.TotalMovements, previousFlow.TotalMovements),
            Compare(currentFlow.TotalDetails, previousFlow.TotalDetails),
            Compare(currentFlow.EntryMovements, previousFlow.EntryMovements),
            Compare(currentFlow.ExitMovements, previousFlow.ExitMovements),
            Compare(currentFlow.TransferMovements, previousFlow.TransferMovements),
            Compare(currentFlow.AdjustmentMovements, previousFlow.AdjustmentMovements));

        var topExitAggregates = await effectiveMovements
            .Where(movement => movement.OccurredAt >= fromUtc && movement.OccurredAt < toUtc &&
                               movement.Type == InventoryMovementType.Exit)
            .SelectMany(movement => movement.Lines.Select(line => new
            {
                line.ProductId,
                line.Product.Sku,
                line.Product.Description,
                UnitCode = line.Unit.Code,
                line.Quantity,
                MovementId = movement.Id
            }))
            .GroupBy(line => new { line.ProductId, line.Sku, line.Description, line.UnitCode })
            .Select(group => new
            {
                group.Key.Sku,
                group.Key.Description,
                group.Key.UnitCode,
                TotalQuantity = group.Sum(line => line.Quantity),
                MovementCount = group.Select(line => line.MovementId).Distinct().Count()
            })
            .OrderByDescending(item => item.TotalQuantity)
            .ThenByDescending(item => item.MovementCount)
            .ThenBy(item => item.Sku)
            .Take(5)
            .ToListAsync(cancellationToken);
        var topExits = topExitAggregates
            .Select(item => new ExecutiveTopSkuDemandDto(
                item.Sku, item.Description, item.UnitCode, item.TotalQuantity, item.MovementCount))
            .ToArray();

        var topStagnant = stagnantExport.Items
            .OrderByDescending(item => item.DaysWithoutExit ?? int.MaxValue)
            .ThenBy(item => item.Sku, StringComparer.Ordinal)
            .Take(5)
            .Select(item => new ExecutiveStagnantSkuDto(
                item.Sku, item.Description, item.UnitCode, item.CurrentStock, item.DaysWithoutExit))
            .ToArray();

        var activityFrom = LocalDate(fromUtc, timeZone);
        var activityTo = LocalDate(toUtc.AddTicks(-1), timeZone);
        var previousFrom = LocalDate(previousFromUtc, timeZone);
        var previousTo = LocalDate(previousToUtc.AddTicks(-1), timeZone);
        var dateQuery = $"period=custom&from={activityFrom:yyyy-MM-dd}&to={activityTo:yyyy-MM-dd}";
        var links = new ExecutiveEvidenceLinksDto(
            "/Reports/Inventory?view=exceptions&exception=minimum",
            "/Reports/Inventory?view=coverage&period=30&coverageClass=Critical",
            "/Reports/Inventory?view=stagnant&stagnantCategory=90plus",
            "/Reports/Inventory?view=exceptions&exception=negative",
            "/Locations?viewMode=racks&kind=rack&rackFilter=occupied",
            "/Locations?viewMode=racks&kind=rack&rackFilter=empty",
            "/Locations?viewMode=table&kind=rack&status=blocked",
            $"/Admin/Inventory/Movements?view=effective&{dateQuery}");

        return new ExecutiveReportDto(
            periodLabel, previousPeriodLabel, generatedAtLocal, timeZone.Id,
            activityFrom, activityTo, previousFrom, previousTo,
            health, capacity, currentFlow, comparison, links, topExits, topStagnant);
    }

    private static async Task<ExecutiveOperationalFlowDto> AggregateFlowAsync(
        IQueryable<InventoryMovement> effectiveMovements,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var aggregate = await effectiveMovements
            .Where(movement => movement.OccurredAt >= fromUtc && movement.OccurredAt < toUtc)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalMovements = group.Count(),
                TotalDetails = group.Sum(movement => movement.Lines.Count),
                EntryMovements = group.Count(movement => movement.Type == InventoryMovementType.Entry),
                EntryDetails = group.Where(movement => movement.Type == InventoryMovementType.Entry).Sum(movement => movement.Lines.Count),
                ExitMovements = group.Count(movement => movement.Type == InventoryMovementType.Exit),
                ExitDetails = group.Where(movement => movement.Type == InventoryMovementType.Exit).Sum(movement => movement.Lines.Count),
                TransferMovements = group.Count(movement => movement.Type == InventoryMovementType.Transfer),
                TransferDetails = group.Where(movement => movement.Type == InventoryMovementType.Transfer).Sum(movement => movement.Lines.Count),
                AdjustmentMovements = group.Count(movement => movement.Type == InventoryMovementType.Adjustment),
                AdjustmentDetails = group.Where(movement => movement.Type == InventoryMovementType.Adjustment).Sum(movement => movement.Lines.Count)
            }).SingleOrDefaultAsync(cancellationToken);

        return aggregate is null
            ? new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
            : new(aggregate.TotalMovements, aggregate.TotalDetails,
                aggregate.EntryMovements, aggregate.EntryDetails,
                aggregate.ExitMovements, aggregate.ExitDetails,
                aggregate.TransferMovements, aggregate.TransferDetails,
                aggregate.AdjustmentMovements, aggregate.AdjustmentDetails);
    }

    private static MetricComparisonDto Compare(int current, int previous)
    {
        var delta = current - previous;
        var state = current == 0 && previous == 0 ? MetricComparisonState.NoActivity :
            previous == 0 ? MetricComparisonState.New :
            delta > 0 ? MetricComparisonState.Increased :
            delta < 0 ? MetricComparisonState.Decreased : MetricComparisonState.Unchanged;
        decimal? percent = previous == 0 ? null : Math.Round(delta * 100m / previous, 1);
        return new(current, previous, delta, percent, state);
    }

    private static DateOnly LocalDate(DateTimeOffset value, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, timeZone).DateTime);

    private static DateTimeOffset ToUtcStart(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local)) local = local.AddMinutes(30);
        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
