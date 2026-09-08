using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Locations.Rack;

public class RackPrintPageModel(
    WarehouseDbContext dbContext,
    WarehouseClock warehouseClock,
    TimeProvider timeProvider) : PageModel
{
    public static readonly short[] KeypadOrder = [7, 8, 9, 4, 5, 6, 1, 2, 3];
    public virtual bool IsAdministrativeView => false;

    public string RackCode { get; private set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; private set; }
    public IReadOnlyList<PositionRow> Positions { get; private set; } = [];
    public int PositionCount => Positions.Count;
    public int OccupiedCount => Positions.Count(item => item.Products.Any(product => product.Quantity != null));
    public int ProductCount => Positions.SelectMany(item => item.Products).Select(item => item.ProductId).Distinct().Count();

    internal RackPrintPageModel(WarehouseDbContext dbContext) : this(
        dbContext,
        new WarehouseClock(new WarehouseSettingsService(dbContext)),
        TimeProvider.System)
    { }

    public async Task<IActionResult> OnGetAsync(string? rowCode, short rackNumber, CancellationToken cancellationToken)
    {
        var normalizedRow = rowCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedRow) || rackNumber <= 0)
            return NotFound();

        var locations = await dbContext.Locations.AsNoTracking()
            .Where(location => location.Kind == LocationKind.Rack && location.IsPhysicallyPresent &&
                location.RowCode == normalizedRow && location.RackNumber == rackNumber)
            .OrderBy(location => location.PalletNumber)
            .Select(location => new LocationSource(location.Id, location.Code, location.PalletNumber!.Value,
                location.Description, location.IsActive, location.IsBlocked, location.BlockReason))
            .ToListAsync(cancellationToken);
        if (locations.Count == 0)
            return NotFound();

        var locationIds = locations.Select(item => item.Id).ToArray();
        var assignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(assignment => assignment.IsActive && locationIds.Contains(assignment.LocationId))
            .Select(assignment => new AssignmentSource(assignment.LocationId, assignment.ProductId,
                assignment.Product.Sku, assignment.Product.Description, assignment.Product.BaseUnit.Code))
            .ToListAsync(cancellationToken);
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => locationIds.Contains(balance.LocationId))
            .Select(balance => new BalanceSource(balance.LocationId, balance.ProductId, balance.Product.Sku,
                balance.Product.Description, balance.Product.BaseUnit.Code, balance.Quantity))
            .ToListAsync(cancellationToken);

        var assignedKeys = assignments.Select(item => (item.LocationId, item.ProductId)).ToHashSet();
        var productRows = balances
            .GroupBy(item => new { item.LocationId, item.ProductId, item.Sku, item.Description, item.Unit })
            .Select(group => new ProductRow(group.Key.ProductId, group.Key.Sku, group.Key.Description,
                group.Key.Unit, group.Sum(item => item.Quantity),
                assignedKeys.Contains((group.Key.LocationId, group.Key.ProductId)) ? "Asignado" : "Saldo sin asignación",
                group.Key.LocationId))
            .Where(item => item.Quantity != 0)
            .Concat(assignments
                .Where(assignment => !balances
                    .Where(balance => balance.LocationId == assignment.LocationId && balance.ProductId == assignment.ProductId)
                    .GroupBy(balance => balance.ProductId)
                    .Any(group => group.Sum(balance => balance.Quantity) != 0))
                .Select(assignment => new ProductRow(assignment.ProductId, assignment.Sku, assignment.Description,
                    assignment.Unit, null, "Asignado sin saldo", assignment.LocationId)))
            .OrderBy(item => item.Sku, StringComparer.Ordinal)
            .ToArray();
        var productsByLocation = productRows.GroupBy(item => item.LocationId)
            .ToDictionary(group => group.Key, group => group.ToArray() as IReadOnlyList<ProductRow>);

        RackCode = $"{normalizedRow}-{rackNumber}";
        GeneratedAt = await warehouseClock.ConvertAsync(timeProvider.GetUtcNow(), cancellationToken);
        Positions = locations.Select(location => new PositionRow(location.Id, location.Code, location.PalletNumber,
            location.Description, location.IsActive, location.IsBlocked, location.BlockReason,
            productsByLocation.GetValueOrDefault(location.Id) ?? [])).ToArray();
        return Page();
    }

    private sealed record LocationSource(Guid Id, string Code, short PalletNumber, string? Description,
        bool IsActive, bool IsBlocked, string? BlockReason);
    private sealed record AssignmentSource(Guid LocationId, Guid ProductId, string Sku, string? Description, string Unit);
    private sealed record BalanceSource(Guid LocationId, Guid ProductId, string Sku, string? Description, string Unit,
        decimal Quantity);
    public sealed record ProductRow(Guid ProductId, string Sku, string? Description, string Unit, decimal? Quantity,
        string RelationshipState, Guid LocationId);
    public sealed record PositionRow(Guid Id, string Code, short PalletNumber, string? Description, bool IsActive,
        bool IsBlocked, string? BlockReason, IReadOnlyList<ProductRow> Products)
    {
        public bool HasIssue => !IsActive || IsBlocked || Products.Any(product => product.Quantity < 0);
        public string OperationalState => !IsActive ? "Inactiva"
            : IsBlocked ? $"Bloqueada: {BlockReason}" : "Disponible";
    }
}
