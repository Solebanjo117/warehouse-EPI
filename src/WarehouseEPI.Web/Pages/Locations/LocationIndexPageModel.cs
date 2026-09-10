using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Locations;

public class LocationIndexPageModel(
    WarehouseDbContext dbContext,
    WarehouseMapService mapService,
    WipReportService wipReportService,
    HeatmapReportService heatmapReportService,
    ReportExportService reportExportService,
    WarehouseClock clock) : PageModel
{
    protected WarehouseDbContext DbContext { get; } = dbContext;

    internal LocationIndexPageModel(WarehouseDbContext context) : this(
        context,
        new WarehouseMapService(context),
        new WipReportService(context, new WarehouseClock(new WarehouseSettingsService(context))),
        new HeatmapReportService(context, new WarehouseMapService(context), new WarehouseSettingsService(context)),
        new ReportExportService(new WarehouseSettingsService(context)),
        new WarehouseClock(new WarehouseSettingsService(context)))
    { }
    private const int PageSize = 25;
    private static readonly short[] KeypadOrder = [7, 8, 9, 4, 5, 6, 1, 2, 3];

    public virtual bool IsAdministrativeView => false;

    public IReadOnlyList<LocationRow> Locations { get; private set; } = [];
    public IReadOnlyList<RackLayout> LayoutRacks { get; private set; } = [];
    public IReadOnlyList<LocationRow> LayoutAreas { get; private set; } = [];
    public IReadOnlyList<string> Rows { get; private set; } = [];
    public LocationSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0);
    public string? Search { get; private set; }
    public string Status { get; private set; } = "active";
    public string Kind { get; private set; } = "all";
    public string RackFilter { get; private set; } = "all";
    public string ViewMode { get; private set; } = "map";
    public WarehouseMapView Map { get; private set; } = new(0, 0, false, [], [], 0, 0, 0, 0, 0, [], [], [], false, null, "IMPERIAL");
    public IReadOnlyDictionary<Guid, IReadOnlyList<WipIssueRow>> RecentWipIssues { get; private set; }
        = new Dictionary<Guid, IReadOnlyList<WipIssueRow>>();
    public IReadOnlySet<Guid> MapMatches { get; private set; } = new HashSet<Guid>();
    public Guid? HighlightLocationId { get; private set; }
    public string? RowCode { get; private set; }
    public string MapMetric { get; private set; } = "normal";
    public string HeatmapPeriod { get; private set; } = "30";
    public DateOnly? HeatmapFrom { get; private set; }
    public DateOnly? HeatmapTo { get; private set; }
    public HeatmapReportDto? Heatmap { get; private set; }
    public string? HeatmapError { get; private set; }
    public IReadOnlyDictionary<Guid, RackHeatmapItemDto> HeatmapByElementId { get; private set; }
        = new Dictionary<Guid, RackHeatmapItemDto>();
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public IReadOnlyList<int> VisiblePages { get; private set; } = [];
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task OnGetAsync(string? search, string status = "active", string kind = "all",
        string viewMode = "map", string? rowCode = null, int pageNumber = 1, Guid? highlightLocationId = null,
        string rackFilter = "all", string mapMetric = "normal", string? period = "30",
        DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        Search = search?.Trim();
        Status = status is "all" or "inactive" or "blocked" or "retired" or "unavailable" ? status : "active";
        Kind = kind is "rack" or "area" ? kind : "all";
        ViewMode = viewMode == "layout" ? "racks" : viewMode is "table" or "racks" ? viewMode : "map";
        RackFilter = rackFilter is "occupied" or "empty" or "issues" ? rackFilter : "all";
        HighlightLocationId = highlightLocationId;
        RowCode = rowCode?.Trim().ToUpperInvariant();
        CurrentPage = Math.Max(1, pageNumber);
        MapMetric = mapMetric is "occupancy" or "activity" ? mapMetric : "normal";

        Rows = await DbContext.Locations.AsNoTracking().Where(location => location.RowCode != null)
            .Select(location => location.RowCode!).Distinct().OrderBy(value => value)
            .ToListAsync(cancellationToken);
        Summary = new(
            await DbContext.Locations.CountAsync(location => location.IsPhysicallyPresent && location.IsActive && !location.IsBlocked, cancellationToken),
            await DbContext.Locations.CountAsync(location => location.IsPhysicallyPresent && location.IsActive && location.IsBlocked, cancellationToken),
            await DbContext.Locations.CountAsync(location => location.IsPhysicallyPresent && !location.IsActive, cancellationToken),
            await DbContext.Locations.CountAsync(location => !location.IsPhysicallyPresent, cancellationToken),
            await DbContext.Locations.CountAsync(location => location.IsPhysicallyPresent && location.Kind == LocationKind.Rack, cancellationToken),
            await DbContext.Locations.CountAsync(location => location.IsPhysicallyPresent && location.Kind == LocationKind.Area, cancellationToken));

        var query = ApplyFilters(DbContext.Locations.AsNoTracking(), ViewMode == "racks");
        if (ViewMode == "map")
        {
            Map = await mapService.GetAsync(true, includeReferences: false, cancellationToken);
            if (MapMetric != "normal")
            {
                var (heatmapFilter, periodLabel) = await BuildHeatmapFilterAsync(period, from, to, cancellationToken);
                try
                {
                    Heatmap = await heatmapReportService.GetHeatmapPageAsync(heatmapFilter, periodLabel, cancellationToken);
                    HeatmapByElementId = Heatmap.AllRacks.ToDictionary(item => item.ElementId);
                }
                catch (Exception)
                {
                    HeatmapError = "No fue posible calcular el mapa de calor. Conserva tus filtros y vuelve a intentarlo.";
                }
            }
            var filteredLocationIds = (await query.Select(location => location.Id).ToListAsync(cancellationToken)).ToHashSet();
            var recentWipIssues = new Dictionary<Guid, IReadOnlyList<WipIssueRow>>();
            foreach (var element in Map.Elements.Where(element => element.IsWip && IsAdministrativeView))
            {
                var wipLocationIds = element.Positions
                    .Where(position => position.OperationalRole == LocationOperationalRole.Wip)
                    .Select(position => position.LocationId)
                    .Distinct()
                    .ToArray();
                recentWipIssues[element.Id] = await wipReportService.GetRecentIssuesAsync(
                    wipLocationIds, 10, cancellationToken);
            }
            RecentWipIssues = recentWipIssues;
            MapMatches = (string.IsNullOrWhiteSpace(Search) ? [] : filteredLocationIds)
                .Append(HighlightLocationId ?? Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
        }
        if (ViewMode == "racks")
        {
            Locations = ApplyRackFilter(await LoadRowsAsync(query.OrderBy(location => location.RowCode)
                .ThenBy(location => location.RackNumber).ThenBy(location => location.PalletNumber), cancellationToken));
            LayoutAreas = [];
            LayoutRacks = CreateRackLayouts(Locations);
            return;
        }

        var count = await query.CountAsync(cancellationToken);
        TotalPages = Math.Max(1, (int)Math.Ceiling(count / (double)PageSize));
        CurrentPage = Math.Min(CurrentPage, TotalPages);
        var firstVisible = Math.Max(1, CurrentPage - 2);
        var lastVisible = Math.Min(TotalPages, CurrentPage + 2);
        VisiblePages = Enumerable.Range(firstVisible, lastVisible - firstVisible + 1).ToArray();

        var ordered = query.OrderBy(location => location.Kind).ThenBy(location => location.RowCode)
            .ThenBy(location => location.RackNumber).ThenBy(location => location.PalletNumber)
            .ThenBy(location => location.Code);
        if (ViewMode == "table")
        {
            Locations = await LoadRowsAsync(ordered.Skip((CurrentPage - 1) * PageSize).Take(PageSize), cancellationToken);
            return;
        }

        Locations = await LoadRowsAsync(ordered, cancellationToken);
        LayoutAreas = Locations.Where(location => location.Kind == LocationKind.Area).ToArray();
        LayoutRacks = CreateRackLayouts(Locations);
    }

    public async Task<IActionResult> OnGetHeatmapExportAsync(
        string? format, string mapMetric = "occupancy", string? period = "30",
        DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        if (!User.IsInRole("ADMIN")) return Forbid();
        if (mapMetric is not ("occupancy" or "activity"))
            return BadRequest("Selecciona una métrica válida para exportar el mapa de calor.");

        MapMetric = mapMetric;
        var (filter, periodLabel) = await BuildHeatmapFilterAsync(period, from, to, cancellationToken);
        var export = await heatmapReportService.GetHeatmapExportAsync(filter, cancellationToken);
        var stamp = export.GeneratedAtLocal.ToString("yyyy-MM-dd");
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return File(await reportExportService.ExportHeatmapToCsvAsync(export.Racks, export.Summary, filter, periodLabel, cancellationToken),
                "text/csv; charset=utf-8", $"Mapa_Calor_{MapMetric}_{stamp}.csv");
        if (!string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest("El formato de exportación debe ser xlsx o csv.");
        return File(await reportExportService.ExportHeatmapToExcelAsync(export.Racks, export.Summary, filter, periodLabel, cancellationToken),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Mapa_Calor_{MapMetric}_{stamp}.xlsx");
    }

    public async Task<IActionResult> OnGetHeatmapDataAsync(
        string mapMetric = "occupancy", string? period = "30",
        DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        if (mapMetric is not ("occupancy" or "activity"))
            return BadRequest(new { error = "Selecciona una métrica válida para el mapa de calor." });

        MapMetric = mapMetric;
        var (filter, periodLabel) = await BuildHeatmapFilterAsync(period, from, to, cancellationToken);
        var report = await heatmapReportService.GetHeatmapPageAsync(filter, periodLabel, cancellationToken);
        return new JsonResult(new
        {
            metric = MapMetric,
            period = HeatmapPeriod,
            from = HeatmapFrom?.ToString("yyyy-MM-dd"),
            to = HeatmapTo?.ToString("yyyy-MM-dd"),
            report.PeriodLabel,
            generatedAtLocal = report.GeneratedAtLocal.ToString("dd/MM/yyyy HH:mm"),
            report.TimeZoneId,
            racks = report.AllRacks.Select(rack => new
            {
                elementId = rack.ElementId.ToString(),
                rack.AccessCount,
                rack.TotalPositions,
                rack.OccupiedPositions,
                rack.OccupancyPercent,
                rack.NegativePositions,
                rack.BlockedPositions,
                rack.HeatLevel
            })
        });
    }

    private async Task<(HeatmapReportFilter Filter, string PeriodLabel)> BuildHeatmapFilterAsync(
        string? period, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var metric = MapMetric == "activity" ? HeatmapMetricType.AccessFrequency : HeatmapMetricType.OccupancyDensity;
        var today = await clock.GetDateAsync(TimeProvider.System.GetUtcNow(), cancellationToken);
        if (metric == HeatmapMetricType.OccupancyDensity)
        {
            HeatmapPeriod = period is "7" or "14" or "30" or "custom" ? period : "30";
            HeatmapFrom = from;
            HeatmapTo = to;
            return (new HeatmapReportFilter(metric, PageSize: 50), "Saldo actual en estantería");
        }

        HeatmapPeriod = period is "7" or "14" or "30" or "custom" ? period : "30";
        if (HeatmapPeriod == "custom" && from.HasValue && to.HasValue)
        {
            HeatmapFrom = from.Value <= to.Value ? from : to;
            HeatmapTo = from.Value <= to.Value ? to : from;
        }
        else
        {
            var days = HeatmapPeriod == "7" ? 7 : HeatmapPeriod == "14" ? 14 : 30;
            HeatmapFrom = today.AddDays(-(days - 1));
            HeatmapTo = today;
            HeatmapPeriod = days.ToString();
        }

        var interval = await clock.GetUtcIntervalAsync(HeatmapFrom, HeatmapTo, cancellationToken);
        var label = HeatmapPeriod == "custom"
            ? $"{HeatmapFrom:dd/MM/yyyy} a {HeatmapTo:dd/MM/yyyy}"
            : $"Últimos {HeatmapPeriod} días";
        return (new HeatmapReportFilter(metric, interval.FromInclusive, interval.ToExclusive, PageSize: 50), label);
    }

    private static IReadOnlyList<RackLayout> CreateRackLayouts(IReadOnlyList<LocationRow> locations)
        => locations.Where(location => location.Kind == LocationKind.Rack)
            .GroupBy(location => (location.RowCode!, location.RackNumber!.Value))
            .OrderBy(group => group.Key.Item1).ThenBy(group => group.Key.Item2)
            .Select(group =>
            {
                var positions = group.ToDictionary(location => location.PalletNumber!.Value);
                return new RackLayout(group.Key.Item1, group.Key.Item2,
                    KeypadOrder.Select(number => positions.GetValueOrDefault(number)).ToArray());
            }).ToArray();

    private IReadOnlyList<LocationRow> ApplyRackFilter(IReadOnlyList<LocationRow> locations)
    {
        if (RackFilter == "all") return locations;
        return locations.GroupBy(location => (location.RowCode, location.RackNumber))
            .Where(group => RackFilter switch
            {
                "occupied" => group.Any(location => location.HasInventory),
                "empty" => group.Any(location => !location.HasInventory),
                "issues" => group.Any(location => location.HasIssue),
                _ => true
            }).SelectMany(group => group).ToArray();
    }

    private IQueryable<Location> ApplyFilters(IQueryable<Location> query, bool forceRack = false)
    {
        if (ViewMode is "map" or "racks") query = query.Where(location => location.IsPhysicallyPresent);
        query = Status switch
        {
            "retired" => query.Where(location => !location.IsPhysicallyPresent),
            "inactive" => query.Where(location => location.IsPhysicallyPresent && !location.IsActive),
            "blocked" => query.Where(location => location.IsPhysicallyPresent && location.IsActive && location.IsBlocked),
            "unavailable" => query.Where(location => !location.IsPhysicallyPresent || !location.IsActive),
            "all" => query,
            _ => query.Where(location => location.IsPhysicallyPresent && location.IsActive && !location.IsBlocked)
        };
        if (forceRack || Kind == "rack") query = query.Where(location => location.Kind == LocationKind.Rack);
        else if (Kind == "area") query = query.Where(location => location.Kind == LocationKind.Area);
        if (!string.IsNullOrWhiteSpace(RowCode)) query = query.Where(location => location.RowCode == RowCode);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.ToUpperInvariant();
            query = query.Where(location => location.Code.Contains(term) ||
                (location.Description != null && location.Description.ToUpper().Contains(term)) ||
                location.ProductAssignments.Any(assignment => assignment.IsActive &&
                    (assignment.Product.Sku.Contains(term) ||
                     (assignment.Product.Description != null && assignment.Product.Description.ToUpper().Contains(term)) ||
                     (assignment.Product.ExternalReference != null && assignment.Product.ExternalReference.ToUpper().Contains(term)) ||
                     assignment.Product.Barcodes.Any(barcode => barcode.IsActive && barcode.Barcode.ToUpper().Contains(term)))) ||
                DbContext.InventoryBalances.Any(balance => balance.LocationId == location.Id && (balance.Product.Sku.Contains(term) ||
                    (balance.Product.Description != null && balance.Product.Description.ToUpper().Contains(term)) ||
                    (balance.Product.ExternalReference != null && balance.Product.ExternalReference.ToUpper().Contains(term)) ||
                    balance.Product.Barcodes.Any(barcode => barcode.IsActive && barcode.Barcode.ToUpper().Contains(term)))));
        }
        return query;
    }

    private async Task<IReadOnlyList<LocationRow>> LoadRowsAsync(
        IQueryable<Location> query,
        CancellationToken cancellationToken)
    {
        var locations = await query.Select(location => new LocationBaseRow(
            location.Id, location.Code, location.Kind, location.RowCode, location.RackNumber,
            location.PalletNumber, location.Description, location.OperationalRole,
            location.IsBlocked, location.BlockReason,
            location.IsActive, location.IsPhysicallyPresent)).ToListAsync(cancellationToken);
        var ids = locations.Select(location => location.Id).ToArray();
        var assignments = ids.Length == 0
            ? []
            : await DbContext.ProductLocationAssignments.AsNoTracking()
                .Where(assignment => assignment.IsActive && ids.Contains(assignment.LocationId))
                .OrderBy(assignment => assignment.Product.Sku)
                .Select(assignment => new AssignmentRow(assignment.LocationId, assignment.ProductId, assignment.Product.Sku))
                .ToListAsync(cancellationToken);
        var byLocation = assignments.GroupBy(assignment => assignment.LocationId)
            .ToDictionary(group => group.Key, group => group.Select(assignment => assignment.Sku).ToArray());
        var balanceSources = ids.Length == 0
            ? []
            : await DbContext.InventoryBalances.AsNoTracking().Where(balance => ids.Contains(balance.LocationId))
                .Select(balance => new BalanceSource(balance.LocationId, balance.ProductId, balance.Product.Sku,
                    balance.Product.Description, balance.Product.BaseUnit.Code, balance.Quantity))
                .ToListAsync(cancellationToken);
        var assignedKeys = assignments.Select(assignment => (assignment.LocationId, assignment.ProductId)).ToHashSet();
        var balanceLookup = balanceSources
            .GroupBy(balance => balance.LocationId)
            .ToDictionary(group => group.Key, group => group.GroupBy(balance => new { balance.ProductId, balance.Sku, balance.Description, balance.Unit })
                .Select(item => new LocationBalance(item.Key.ProductId, item.Key.Sku, item.Key.Description, item.Key.Unit,
                    item.Sum(balance => balance.Quantity), assignedKeys.Contains((group.Key, item.Key.ProductId))))
                .Where(item => item.Quantity != 0).OrderBy(item => item.Sku, StringComparer.Ordinal).ToArray() as IReadOnlyList<LocationBalance>);
        return locations.Select(location =>
        {
            var skus = byLocation.GetValueOrDefault(location.Id) ?? [];
            return new LocationRow(location.Id, location.Code, location.Kind, location.RowCode,
                location.RackNumber, location.PalletNumber, location.Description, location.OperationalRole,
                location.IsBlocked,
                location.BlockReason, location.IsActive, location.IsPhysicallyPresent,
                skus.Take(3).ToArray(), skus.Length,
                balanceLookup.GetValueOrDefault(location.Id) ?? []);
        }).ToArray();
    }

    private sealed record LocationBaseRow(Guid Id, string Code, LocationKind Kind, string? RowCode,
        short? RackNumber, short? PalletNumber, string? Description,
        LocationOperationalRole OperationalRole, bool IsBlocked,
        string? BlockReason, bool IsActive, bool IsPhysicallyPresent);
    private sealed record AssignmentRow(Guid LocationId, Guid ProductId, string Sku);
    private sealed record BalanceSource(Guid LocationId, Guid ProductId, string Sku, string? Description, string Unit, decimal Quantity);
    public sealed record LocationBalance(Guid ProductId, string Sku, string? Description, string Unit, decimal Quantity, bool IsAssigned);
    public sealed record LocationRow(Guid Id, string Code, LocationKind Kind, string? RowCode,
        short? RackNumber, short? PalletNumber, string? Description,
        LocationOperationalRole OperationalRole, bool IsBlocked,
        string? BlockReason, bool IsActive, bool IsPhysicallyPresent,
        IReadOnlyList<string> Skus, int ProductCount, IReadOnlyList<LocationBalance> Balances)
    {
        public bool HasInventory => Balances.Count > 0;
        public bool IsWip => OperationalRole == LocationOperationalRole.Wip;
        public bool HasNegative => Balances.Any(balance => balance.Quantity < 0);
        public bool HasIssue => !IsActive || IsBlocked || HasNegative;
        public string RackState => HasNegative ? "negative" : !IsActive ? "inactive" : IsBlocked ? "blocked" : HasInventory ? "occupied" : "empty";
    }
    public sealed record RackLayout(string RowCode, short RackNumber, IReadOnlyList<LocationRow?> Positions)
    {
        private IEnumerable<LocationRow> Existing => Positions.OfType<LocationRow>();
        public int PositionCount => Existing.Count();
        public int OccupiedCount => Existing.Count(position => position.HasInventory);
        public int EmptyCount => Existing.Count(position => !position.HasInventory);
        public int IssueCount => Existing.Count(position => position.HasIssue);
        public bool IsWip => Existing.Any() && Existing.All(position => position.IsWip);
        public string RackState => Existing.Any(position => position.HasNegative) ? "negative" : Existing.Any(position => !position.IsActive) ? "inactive" : Existing.Any(position => position.IsBlocked) ? "blocked" : Existing.Any(position => position.HasInventory) ? "occupied" : "empty";
    }
    public sealed record LocationSummary(int Available, int Blocked, int Inactive, int Retired, int Racks, int Areas);
}
