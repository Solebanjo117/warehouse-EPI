using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>
/// Servicio analítico del mapa de calor sobre el croquis SVG del almacén.
/// Calcula la intensidad de uso (frecuencia de picking/movimientos) o densidad de ocupación por rack.
/// </summary>
public sealed class HeatmapReportService(
    WarehouseDbContext dbContext,
    WarehouseMapService mapService,
    WarehouseSettingsService settingsService,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<HeatmapReportDto> GetHeatmapPageAsync(
        HeatmapReportFilter filter,
        string periodLabel = "",
        CancellationToken cancellationToken = default)
    {
        var (timeZone, generatedAtLocal) = await GetTimeZoneAndLocalNowAsync(cancellationToken);
        var (racks, summary, canvasWidth, canvasHeight) = await ComputeHeatmapAsync(filter, cancellationToken);

        var pageNumber = Math.Max(1, filter.PageNumber);
        var pageSize = filter.PageSize <= 0 ? 50 : Math.Clamp(filter.PageSize, 1, 200);
        var pagedItems = racks
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        return new HeatmapReportDto(
            filter.Metric,
            periodLabel,
            generatedAtLocal,
            timeZone.Id,
            canvasWidth,
            canvasHeight,
            summary,
            pagedItems,
            racks.Count,
            pageNumber,
            pageSize)
        {
            AllRacks = racks
        };
    }

    public async Task<(IReadOnlyList<RackHeatmapItemDto> Racks, HeatmapSummaryDto Summary, DateTimeOffset GeneratedAtLocal, string TimeZoneId, decimal CanvasWidth, decimal CanvasHeight)> GetHeatmapExportAsync(
        HeatmapReportFilter filter,
        CancellationToken cancellationToken = default)
    {
        var (timeZone, generatedAtLocal) = await GetTimeZoneAndLocalNowAsync(cancellationToken);
        var (racks, summary, canvasWidth, canvasHeight) = await ComputeHeatmapAsync(filter, cancellationToken);

        return (racks, summary, generatedAtLocal, timeZone.Id, canvasWidth, canvasHeight);
    }

    private async Task<(List<RackHeatmapItemDto> Racks, HeatmapSummaryDto Summary, decimal CanvasWidth, decimal CanvasHeight)> ComputeHeatmapAsync(
        HeatmapReportFilter filter,
        CancellationToken cancellationToken)
    {
        var mapView = await mapService.GetAsync(includeProposal: true, cancellationToken);
        var rackElements = mapView.Elements.Concat(mapView.Unplaced)
            .Where(e => e.Kind == "Rack")
            .DistinctBy(e => e.Id)
            .ToList();

        Dictionary<Guid, int> accessCountsByElement = [];
        var elementByLocation = rackElements
            .SelectMany(element => element.Positions.Select(position => new { position.LocationId, element.Id }))
            .GroupBy(item => item.LocationId)
            .ToDictionary(group => group.Key, group => group.First().Id);
        var locationIds = elementByLocation.Keys.ToArray();

        if (filter.Metric == HeatmapMetricType.AccessFrequency)
        {
            var changesQuery = dbContext.InventoryMovements
                .AsNoTracking()
                .WhereEffective(dbContext);

            if (filter.FromUtc.HasValue)
                changesQuery = changesQuery.Where(m => m.OccurredAt >= filter.FromUtc.Value);

            if (filter.ToUtc.HasValue)
                changesQuery = changesQuery.Where(m => m.OccurredAt < filter.ToUtc.Value);

            var accesses = await changesQuery
                .SelectMany(m => m.Lines.SelectMany(l => l.BalanceChanges.Select(c => new
                {
                    MovementId = m.Id,
                    c.LocationId
                })))
                .Where(item => locationIds.Contains(item.LocationId))
                .Distinct()
                .ToListAsync(cancellationToken);

            accessCountsByElement = accesses
                .Select(item => new { item.MovementId, ElementId = elementByLocation[item.LocationId] })
                .Distinct()
                .GroupBy(item => item.ElementId)
                .ToDictionary(group => group.Key, group => group.Count());
        }

        var productBalances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => locationIds.Contains(balance.LocationId))
            .GroupBy(balance => new { balance.LocationId, balance.ProductId })
            .Select(group => new { group.Key.LocationId, Quantity = group.Sum(balance => balance.Quantity) })
            .ToListAsync(cancellationToken);
        var balanceStates = productBalances.GroupBy(item => item.LocationId).ToDictionary(
            group => group.Key,
            group => new
            {
                HasPositive = group.Any(item => item.Quantity > 0m),
                HasNegative = group.Any(item => item.Quantity < 0m)
            });

        var rackItems = new List<RackHeatmapItemDto>(rackElements.Count);

        foreach (var element in rackElements)
        {
            var activePositions = element.Positions.Where(p => p.IsActive).ToList();
            var totalPos = activePositions.Count;
            var occupiedPos = activePositions.Count(p => balanceStates.GetValueOrDefault(p.LocationId)?.HasPositive == true);
            var negativePos = activePositions.Count(p => balanceStates.GetValueOrDefault(p.LocationId)?.HasNegative == true);
            var blockedPos = activePositions.Count(p => p.IsBlocked);
            var occupancyPct = totalPos == 0 ? 0m : Math.Round((decimal)occupiedPos * 100m / totalPos, 2);

            var accessCount = 0;
            if (filter.Metric == HeatmapMetricType.AccessFrequency)
            {
                accessCount = accessCountsByElement.GetValueOrDefault(element.Id, 0);
            }

            rackItems.Add(new RackHeatmapItemDto(
                element.Id,
                element.Label,
                element.RowCode ?? "",
                element.RackNumber,
                accessCount,
                totalPos,
                occupiedPos,
                occupancyPct,
                0,
                "heat-0")
            {
                NegativePositions = negativePos,
                BlockedPositions = blockedPos
            });
        }

        // Asignar niveles térmicos (0 a 4)
        if (filter.Metric == HeatmapMetricType.AccessFrequency)
        {
            var maxAccess = rackItems.Count == 0 ? 0 : rackItems.Max(r => r.AccessCount);
            rackItems = rackItems.Select(r =>
            {
                var level = 0;
                if (maxAccess > 0 && r.AccessCount > 0)
                {
                    var ratio = (decimal)r.AccessCount / maxAccess;
                    level = ratio switch
                    {
                        <= 0.25m => 1,
                        <= 0.50m => 2,
                        <= 0.75m => 3,
                        _ => 4
                    };
                }
                return r with { HeatLevel = level, HeatClass = $"heat-{level}" };
            }).ToList();
        }
        else
        {
            rackItems = rackItems.Select(r =>
            {
                var level = 0;
                if (r.OccupiedPositions > 0 && r.TotalPositions > 0)
                {
                    level = r.OccupancyPercent switch
                    {
                        <= 25m => 1,
                        <= 50m => 2,
                        <= 75m => 3,
                        _ => 4
                    };
                }
                return r with { HeatLevel = level, HeatClass = $"heat-{level}" };
            }).ToList();
        }

        // Filtros opcionales de fila y búsqueda
        var filteredRacks = rackItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.RowCode))
        {
            filteredRacks = filteredRacks.Where(r => string.Equals(r.RowCode, filter.RowCode.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            filteredRacks = filteredRacks.Where(r =>
                r.Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.RowCode.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var resultRacks = filteredRacks
            .OrderBy(r => r.RowCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RackNumber ?? 0)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalRacks = resultRacks.Count;
        var activeRacks = resultRacks.Count(r => r.TotalPositions > 0);
        var maxAccessCount = resultRacks.Count == 0 ? 0 : resultRacks.Max(r => r.AccessCount);
        var averageOccupancy = activeRacks == 0 ? 0m : Math.Round(resultRacks.Sum(r => r.OccupancyPercent) / activeRacks, 2);
        var highHeatCount = resultRacks.Count(r => r.HeatLevel >= 3);

        var summary = new HeatmapSummaryDto(
            totalRacks,
            activeRacks,
            maxAccessCount,
            averageOccupancy,
            highHeatCount);

        return (resultRacks, summary, mapView.CanvasWidth, mapView.CanvasHeight);
    }

    private async Task<(TimeZoneInfo TimeZone, DateTimeOffset GeneratedAtLocal)> GetTimeZoneAndLocalNowAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var nowUtc = _timeProvider.GetUtcNow();
        var generatedAtLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        return (timeZone, generatedAtLocal);
    }
}
