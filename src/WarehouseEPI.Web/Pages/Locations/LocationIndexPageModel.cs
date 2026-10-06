using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Locations;

public class LocationIndexPageModel(
    WarehouseDbContext dbContext,
    WarehouseMapService mapService,
    WipReportService wipReportService,
    HeatmapReportService heatmapReportService,
    ReportExportService reportExportService,
    WarehouseClock clock, IStringLocalizer<CatalogTexts>? localizer = null) : PageModel
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

    public virtual bool IsAdministrativeView => false;

    public IReadOnlyDictionary<(string, short), RackWipAssociationView> WipAssociations { get; private set; } = new Dictionary<(string, short), RackWipAssociationView>();
    public RackWipAssociationView? RackWip(string? row, short? rack) => WipAssociations.GetValueOrDefault((row ?? "", rack ?? 0));
    public IReadOnlyDictionary<Guid, IReadOnlyList<WipInventoryRow>> AssociatedWipDeliveries { get; private set; }
        = new Dictionary<Guid, IReadOnlyList<WipInventoryRow>>();
    public sealed record AssociatedWipSummary(RackWipAssociationView Association, IReadOnlyList<WipInventoryRow> Deliveries);
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
        WipAssociations = await LocationRackWipAssociations.LoadAsync(DbContext, cancellationToken);
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
            // Read each shared area once, even when several mixed racks reference it.
            var associatedDeliveries = new Dictionary<Guid, IReadOnlyList<WipInventoryRow>>();
            var areaIds = Map.Elements
                .Where(element => User.IsInRole("ADMIN") && element.Kind == "Rack" && element.Positions.Any(position => position.OperationalRole == LocationOperationalRole.Wip))
                .Select(element => RackWip(element.RowCode, element.RackNumber)?.WipAreaId)
                .OfType<Guid>().Distinct();
            foreach (var areaId in areaIds)
                associatedDeliveries[areaId] = (await wipReportService.GetTrackedPageAsync(
                    new(null, null, WipAreaId: areaId), 1, 5, cancellationToken)).Inventory;
            AssociatedWipDeliveries = associatedDeliveries;
            if (MapMetric != "normal")
            {
                var heatmapQuery = await HeatmapQueryNormalizer.BuildAsync(
                    MapMetric, period, from, to, clock, cancellationToken: cancellationToken, texts: localizer ?? HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CatalogTexts>>());
                ApplyHeatmapQuery(heatmapQuery);
                try
                {
                    Heatmap = await heatmapReportService.GetHeatmapPageAsync(
                        heatmapQuery.Filter, heatmapQuery.PeriodLabel, cancellationToken);
                    HeatmapByElementId = Heatmap.AllRacks.ToDictionary(item => item.ElementId);
                }
                catch (Exception)
                {
                    HeatmapError = (localizer ?? HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CatalogTexts>>())["No fue posible calcular el mapa de calor. Conserva tus filtros y vuelve a intentarlo."].Value;
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
            LayoutRacks = await CreateRackLayoutsAsync(Locations, cancellationToken);
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
        LayoutRacks = await CreateRackLayoutsAsync(Locations, cancellationToken);
    }

    public async Task<IActionResult> OnGetHeatmapExportAsync(
        string? format, string mapMetric = "occupancy", string? period = "30",
        DateOnly? from = null, DateOnly? to = null, string? rowCode = null, string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!User.IsInRole("ADMIN")) return Forbid();
        if (!HeatmapQueryNormalizer.IsCanonicalMetric(mapMetric))
            return BadRequest((localizer ?? HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CatalogTexts>>())["Selecciona una métrica válida para exportar el mapa de calor."]);

        var heatmapQuery = await HeatmapQueryNormalizer.BuildAsync(
            mapMetric, period, from, to, clock, rowCode, search,
            localizer ?? HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CatalogTexts>>(), cancellationToken);
        ApplyHeatmapQuery(heatmapQuery);
        var export = await heatmapReportService.GetHeatmapExportAsync(heatmapQuery.Filter, cancellationToken);
        var stamp = export.GeneratedAtLocal.ToString("yyyy-MM-dd");
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return File(await reportExportService.ExportHeatmapToCsvAsync(
                    export.Racks, export.Summary, heatmapQuery.Filter, heatmapQuery.PeriodLabel, cancellationToken),
                "text/csv; charset=utf-8", $"Mapa_Calor_{MapMetric}_{stamp}.csv");
        if (!string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest((localizer ?? HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CatalogTexts>>())["El formato de exportación debe ser xlsx o csv."]);
        return File(await reportExportService.ExportHeatmapToExcelAsync(
                export.Racks, export.Summary, heatmapQuery.Filter, heatmapQuery.PeriodLabel, cancellationToken),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Mapa_Calor_{MapMetric}_{stamp}.xlsx");
    }

    public async Task<IActionResult> OnGetHeatmapDataAsync(
        string mapMetric = "occupancy", string? period = "30",
        DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        if (!HeatmapQueryNormalizer.IsCanonicalMetric(mapMetric))
            return BadRequest(new { error = (localizer ?? HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CatalogTexts>>())["Selecciona una métrica válida para el mapa de calor."].Value });

        var heatmapQuery = await HeatmapQueryNormalizer.BuildAsync(
            mapMetric, period, from, to, clock, cancellationToken: cancellationToken,
            texts: localizer ?? HttpContext.RequestServices.GetRequiredService<IStringLocalizer<CatalogTexts>>());
        ApplyHeatmapQuery(heatmapQuery);
        var report = await heatmapReportService.GetHeatmapPageAsync(
            heatmapQuery.Filter, heatmapQuery.PeriodLabel, cancellationToken);
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

    private void ApplyHeatmapQuery(HeatmapQueryState query)
    {
        MapMetric = query.MapMetric;
        HeatmapPeriod = query.Period;
        HeatmapFrom = query.From;
        HeatmapTo = query.To;
    }

    private async Task<IReadOnlyList<RackLayout>> CreateRackLayoutsAsync(IReadOnlyList<LocationRow> locations,
        CancellationToken token)
    {
        var formats = await LocationRackFormats.LoadAsync(DbContext, token);
        return locations.Where(location => location.Kind == LocationKind.Rack)
            .GroupBy(location => (location.RowCode!, location.RackNumber!.Value))
            .OrderBy(group => group.Key.Item1).ThenBy(group => group.Key.Item2)
            .Select(group =>
            {
                var positions = group.ToDictionary(location => location.PalletNumber!.Value);
                var format = formats.GetValueOrDefault(group.Key) ?? RackFormat.Default;
                return new RackLayout(group.Key.Item1, group.Key.Item2,
                    format.PalletOrder.Select(number => positions.GetValueOrDefault(number)).ToArray())
                { Format = format };
            }).ToArray();
    }

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
            var matchingProducts = DbContext.Products.Where(product => product.Sku.Contains(term) ||
                (product.Description != null && product.Description.ToUpper().Contains(term)) ||
                (product.ExternalReference != null && product.ExternalReference.ToUpper().Contains(term)) ||
                product.Barcodes.Any(barcode => barcode.IsActive && barcode.Barcode.ToUpper().Contains(term)))
                .Select(product => product.Id);
            // Presence is the net balance of each product at the location, across all lots.
            var locationsWithBalance = DbContext.InventoryBalances
                .Where(balance => matchingProducts.Contains(balance.ProductId))
                .GroupBy(balance => new { balance.LocationId, balance.ProductId })
                .Where(group => group.Sum(balance => balance.Quantity) != 0)
                .Select(group => group.Key.LocationId);
            var includeAssignments = IsAdministrativeView;
            query = query.Where(location => location.Code.Contains(term) ||
                (location.Description != null && location.Description.ToUpper().Contains(term)) ||
                (location.OperationalRole != LocationOperationalRole.Wip &&
                (locationsWithBalance.Contains(location.Id) ||
                 (includeAssignments && location.ProductAssignments.Any(assignment => assignment.IsActive &&
                     matchingProducts.Contains(assignment.ProductId))))));
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
            : await DbContext.ProductLocationAssignments.AsNoTracking().Where(x => x.Location.OperationalRole != LocationOperationalRole.Wip)
                .Where(assignment => assignment.IsActive && ids.Contains(assignment.LocationId))
                .OrderBy(assignment => assignment.Product.Sku)
                .Select(assignment => new AssignmentRow(assignment.LocationId, assignment.ProductId, assignment.Product.Sku))
                .ToListAsync(cancellationToken);
        var byLocation = assignments.GroupBy(assignment => assignment.LocationId)
            .ToDictionary(group => group.Key, group => group.Select(assignment => assignment.Sku).ToArray());
        var balanceSources = ids.Length == 0
            ? []
            : await DbContext.InventoryBalances.AsNoTracking().Where(x => x.Location.OperationalRole != LocationOperationalRole.Wip).Where(balance => ids.Contains(balance.LocationId))
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
        public RackFormat Format { get; init; } = RackFormat.Default;
        private IEnumerable<LocationRow> Existing => Positions.OfType<LocationRow>();
        public int PositionCount => Existing.Count();
        public int StoragePositionCount => Existing.Count(position => !position.IsWip);
        public int OccupiedCount => Existing.Count(position => !position.IsWip && position.HasInventory);
        public int EmptyCount => Existing.Count(position => !position.IsWip && !position.HasInventory);
        public int IssueCount => Existing.Count(position => position.HasIssue);
        public bool IsWip => Existing.Any() && Existing.All(position => position.IsWip);
        public bool IsMixed => Existing.Any(position => position.IsWip) && Existing.Any(position => !position.IsWip);
        public string RackState => Existing.Any(position => position.HasNegative) ? "negative" : Existing.Any(position => !position.IsActive) ? "inactive" : Existing.Any(position => position.IsBlocked) ? "blocked" : Existing.Any(position => position.HasInventory) ? "occupied" : "empty";
    }
    public sealed record LocationSummary(int Available, int Blocked, int Inactive, int Retired, int Racks, int Areas);
}
