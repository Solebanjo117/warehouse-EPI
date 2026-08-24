using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(
    WarehouseDbContext dbContext,
    WarehouseMapService mapService,
    WipReportService wipReportService) : PageModel
{
    internal IndexModel(WarehouseDbContext dbContext) : this(
        dbContext,
        new WarehouseMapService(dbContext),
        new WipReportService(dbContext, new WarehouseClock(new WarehouseSettingsService(dbContext))))
    { }
    private const int PageSize = 25;
    private static readonly short[] KeypadOrder = [7, 8, 9, 4, 5, 6, 1, 2, 3];

    public IReadOnlyList<LocationRow> Locations { get; private set; } = [];
    public IReadOnlyList<RackLayout> LayoutRacks { get; private set; } = [];
    public IReadOnlyList<LocationRow> LayoutAreas { get; private set; } = [];
    public IReadOnlyList<string> Rows { get; private set; } = [];
    public LocationSummary Summary { get; private set; } = new(0, 0, 0, 0, 0);
    public string? Search { get; private set; }
    public string Status { get; private set; } = "active";
    public string Kind { get; private set; } = "all";
    public string RackFilter { get; private set; } = "all";
    public string ViewMode { get; private set; } = "map";
    public WarehouseMapView Map { get; private set; } = new(0, 0, false, [], [], 0, 0, 0, 0, 0, [], [], false);
    public IReadOnlyDictionary<Guid, IReadOnlyList<WipIssueRow>> RecentWipIssues { get; private set; }
        = new Dictionary<Guid, IReadOnlyList<WipIssueRow>>();
    public IReadOnlySet<Guid> MapMatches { get; private set; } = new HashSet<Guid>();
    public Guid? HighlightLocationId { get; private set; }
    public string? RowCode { get; private set; }
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public IReadOnlyList<int> VisiblePages { get; private set; } = [];
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task OnGetAsync(string? search, string status = "active", string kind = "all",
        string viewMode = "map", string? rowCode = null, int pageNumber = 1, Guid? highlightLocationId = null,
        string rackFilter = "all", CancellationToken cancellationToken = default)
    {
        Search = search?.Trim();
        Status = status is "all" or "inactive" or "blocked" ? status : "active";
        Kind = kind is "rack" or "area" ? kind : "all";
        ViewMode = viewMode == "layout" ? "racks" : viewMode is "table" or "racks" ? viewMode : "map";
        RackFilter = rackFilter is "occupied" or "empty" or "issues" ? rackFilter : "all";
        HighlightLocationId = highlightLocationId;
        RowCode = rowCode?.Trim().ToUpperInvariant();
        CurrentPage = Math.Max(1, pageNumber);

        Rows = await dbContext.Locations.AsNoTracking().Where(location => location.RowCode != null)
            .Select(location => location.RowCode!).Distinct().OrderBy(value => value)
            .ToListAsync(cancellationToken);
        Summary = new(
            await dbContext.Locations.CountAsync(location => location.IsActive && !location.IsBlocked, cancellationToken),
            await dbContext.Locations.CountAsync(location => location.IsActive && location.IsBlocked, cancellationToken),
            await dbContext.Locations.CountAsync(location => !location.IsActive, cancellationToken),
            await dbContext.Locations.CountAsync(location => location.Kind == LocationKind.Rack, cancellationToken),
            await dbContext.Locations.CountAsync(location => location.Kind == LocationKind.Area, cancellationToken));

        var query = ApplyFilters(dbContext.Locations.AsNoTracking(), ViewMode == "racks");
        if (ViewMode == "map")
        {
            Map = await mapService.GetAsync(true, cancellationToken);
            var recentWipIssues = new Dictionary<Guid, IReadOnlyList<WipIssueRow>>();
            foreach (var wipAreaId in Map.Elements.Where(element => element.IsWip)
                         .Select(element => element.LocationId)
                         .OfType<Guid>()
                         .Distinct())
            {
                recentWipIssues[wipAreaId] = await wipReportService.GetRecentIssuesAsync(
                    wipAreaId, 10, cancellationToken);
            }
            RecentWipIssues = recentWipIssues;
            MapMatches = (string.IsNullOrWhiteSpace(Search) ? [] : await query.Select(location => location.Id).ToListAsync(cancellationToken)).Append(HighlightLocationId ?? Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
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

    public async Task<IActionResult> OnPostToggleAsync(Guid id, CancellationToken cancellationToken)
    {
        var location = await dbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (location is null) return NotFound();
        location.IsActive = !location.IsActive;
        location.IsBlocked = false;
        location.BlockReason = null;
        location.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        Message = location.IsActive ? $"{location.Code} fue activada." : $"{location.Code} fue desactivada.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBlockAsync(Guid id, string? reason, CancellationToken cancellationToken)
    {
        var location = await dbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (location is null) return NotFound();
        var normalizedReason = reason?.Trim();
        if (!location.IsActive) Error = "No se puede bloquear una ubicación inactiva.";
        else if (string.IsNullOrWhiteSpace(normalizedReason) || normalizedReason.Length > 200)
            Error = "Escribe un motivo de bloqueo de hasta 200 caracteres.";
        else
        {
            location.IsBlocked = true;
            location.BlockReason = normalizedReason;
            location.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            Message = $"{location.Code} fue bloqueada.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnblockAsync(Guid id, CancellationToken cancellationToken)
    {
        var location = await dbContext.Locations.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (location is null) return NotFound();
        location.IsBlocked = false;
        location.BlockReason = null;
        location.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        Message = $"{location.Code} fue desbloqueada.";
        return RedirectToPage();
    }

    private IQueryable<Location> ApplyFilters(IQueryable<Location> query, bool forceRack = false)
    {
        query = Status switch
        {
            "inactive" => query.Where(location => !location.IsActive),
            "blocked" => query.Where(location => location.IsActive && location.IsBlocked),
            "all" => query,
            _ => query.Where(location => location.IsActive && !location.IsBlocked)
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
                dbContext.InventoryBalances.Any(balance => balance.LocationId == location.Id && (balance.Product.Sku.Contains(term) ||
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
            location.PalletNumber, location.Description, location.IsBlocked, location.BlockReason,
            location.IsActive)).ToListAsync(cancellationToken);
        var ids = locations.Select(location => location.Id).ToArray();
        var assignments = ids.Length == 0
            ? []
            : await dbContext.ProductLocationAssignments.AsNoTracking()
                .Where(assignment => assignment.IsActive && ids.Contains(assignment.LocationId))
                .OrderBy(assignment => assignment.Product.Sku)
                .Select(assignment => new AssignmentRow(assignment.LocationId, assignment.ProductId, assignment.Product.Sku))
                .ToListAsync(cancellationToken);
        var byLocation = assignments.GroupBy(assignment => assignment.LocationId)
            .ToDictionary(group => group.Key, group => group.Select(assignment => assignment.Sku).ToArray());
        var balanceSources = ids.Length == 0
            ? []
            : await dbContext.InventoryBalances.AsNoTracking().Where(balance => ids.Contains(balance.LocationId))
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
                location.RackNumber, location.PalletNumber, location.Description, location.IsBlocked,
                location.BlockReason, location.IsActive, skus.Take(3).ToArray(), skus.Length,
                balanceLookup.GetValueOrDefault(location.Id) ?? []);
        }).ToArray();
    }

    private sealed record LocationBaseRow(Guid Id, string Code, LocationKind Kind, string? RowCode,
        short? RackNumber, short? PalletNumber, string? Description, bool IsBlocked,
        string? BlockReason, bool IsActive);
    private sealed record AssignmentRow(Guid LocationId, Guid ProductId, string Sku);
    private sealed record BalanceSource(Guid LocationId, Guid ProductId, string Sku, string? Description, string Unit, decimal Quantity);
    public sealed record LocationBalance(Guid ProductId, string Sku, string? Description, string Unit, decimal Quantity, bool IsAssigned);
    public sealed record LocationRow(Guid Id, string Code, LocationKind Kind, string? RowCode,
        short? RackNumber, short? PalletNumber, string? Description, bool IsBlocked,
        string? BlockReason, bool IsActive, IReadOnlyList<string> Skus, int ProductCount, IReadOnlyList<LocationBalance> Balances)
    {
        public bool HasInventory => Balances.Count > 0;
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
        public string RackState => Existing.Any(position => position.HasNegative) ? "negative" : Existing.Any(position => !position.IsActive) ? "inactive" : Existing.Any(position => position.IsBlocked) ? "blocked" : Existing.Any(position => position.HasInventory) ? "occupied" : "empty";
    }
    public sealed record LocationSummary(int Available, int Blocked, int Inactive, int Racks, int Areas);
}
