using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Locations;

public sealed record DisplayRackView(DisplayRack Rack, bool ShowLabel = false, bool Detailed = false);
public sealed record DisplayInspection(string Label, DisplayRack? Rack, IReadOnlyList<DisplayPosition> Positions,
    string UpdatedAt);
public sealed record DisplayProductLocation(Guid LocationId, string Code, string? Description, string Kind,
    string? RowCode, short? RackNumber, short? PalletNumber, decimal Quantity, string Unit,
    bool HasActiveAssignment, bool HasNonZeroBalance, bool IsActive, bool IsBlocked, bool IsWip);
public sealed record DisplayProductLocations(OperationalProductResult Product,
    IReadOnlyList<DisplayProductLocation> Locations, string UpdatedAt);

public sealed partial class DisplayModel
{
    public async Task<IActionResult> OnGetProductsAsync(string? q, CancellationToken cancellationToken = default)
    {
        var query = new OperationalInventoryQueryService(db);
        var exact = await query.ResolveProductAsync(q, activeOnly: false, cancellationToken);
        var products = exact is null ? (await query.SearchInventoryAsync(q, cancellationToken)).Products : [exact];
        return new JsonResult(products);
    }

    public async Task<IActionResult> OnGetProductLocationsAsync(Guid productId,
        CancellationToken cancellationToken = default)
    {
        if (productId == Guid.Empty) return BadRequest();
        var product = await new OperationalInventoryQueryService(db).GetProductAsync(productId,
            activeOnly: false, cancellationToken);
        if (product is null) return NotFound();
        var inventory = await new InventoryQueryService(db).GetProductInventoryAsync(productId, cancellationToken);
        var ids = inventory.Select(item => item.LocationId).ToArray();
        var locations = await db.Locations.AsNoTracking().Where(item => ids.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var results = inventory.Select(item =>
        {
            var location = locations[item.LocationId];
            return new DisplayProductLocation(item.LocationId, item.LocationCode, item.LocationDescription,
                location.Kind.ToString(), location.RowCode, location.RackNumber, location.PalletNumber,
                item.Quantity, item.UnitCode, item.HasActiveAssignment, item.HasNonZeroBalance,
                item.LocationIsActive, item.LocationIsBlocked, location.IsWip);
        }).OrderBy(item => item.RowCode, StringComparer.Ordinal).ThenBy(item => item.RackNumber)
            .ThenBy(item => item.PalletNumber).ThenBy(item => item.Code, StringComparer.Ordinal).ToArray();
        return new JsonResult(new DisplayProductLocations(product, results, await UpdatedAtAsync(cancellationToken)));
    }

    public async Task<IActionResult> OnGetInspectAsync(string? rowCode, short? rackNumber, Guid? locationId,
        CancellationToken cancellationToken = default)
    {
        Location? selected = null;
        if (locationId.HasValue)
        {
            if (locationId == Guid.Empty) return BadRequest();
            selected = await db.Locations.AsNoTracking().SingleOrDefaultAsync(item =>
                item.Id == locationId && item.IsPhysicallyPresent, cancellationToken);
            if (selected is null) return NotFound();
            rowCode = selected.RowCode;
            rackNumber = selected.RackNumber;
        }
        var row = rowCode?.Trim().ToUpperInvariant();
        if (selected?.Kind != LocationKind.Area && (string.IsNullOrEmpty(row) || rackNumber is null or <= 0))
            return BadRequest();
        var locations = selected?.Kind == LocationKind.Area ? new[] { selected } :
            await db.Locations.AsNoTracking().Where(item => item.IsPhysicallyPresent && item.Kind == LocationKind.Rack &&
                item.RowCode == row && item.RackNumber == rackNumber).OrderBy(item => item.PalletNumber)
                .ToArrayAsync(cancellationToken);
        if (locations.Length == 0) return NotFound();
        var products = await LoadDisplayProductsAsync(locations.Select(item => item.Id).ToArray(), true, cancellationToken);
        DisplayPosition Position(Location item) => new(item.PalletNumber ?? 0, item.Code,
            products.GetValueOrDefault(item.Id)?.Any(product => product.Quantity < 0) == true ? "negative" :
            !item.IsActive ? "inactive" : item.IsBlocked ? "blocked" :
            products.GetValueOrDefault(item.Id)?.Any(product => product.HasNonZeroBalance) == true ? "occupied" : "empty",
            item.IsWip, products.GetValueOrDefault(item.Id) ?? [], item.Id, item.IsActive, item.IsBlocked);
        DisplayRack? rack = null;
        IReadOnlyList<DisplayPosition> positions;
        if (selected?.Kind == LocationKind.Area) positions = [Position(selected)];
        else
        {
            positions = KeypadOrder.Select(number => locations.FirstOrDefault(item => item.PalletNumber == number) is { } item
                ? Position(item) : new DisplayPosition(number, null, "missing", false, [])).ToArray();
            rack = new(row!, rackNumber!.Value, locations.Length,
                positions.Count(item => item.Products.Any(product => product.HasNonZeroBalance)), positions);
        }
        return Partial("_DisplayInspection", new DisplayInspection(rack?.Label ?? selected!.Code, rack, positions,
            await UpdatedAtAsync(cancellationToken)));
    }

    private async Task<Dictionary<Guid, IReadOnlyList<DisplayProduct>>> LoadDisplayProductsAsync(
        IReadOnlyCollection<Guid> locationIds, bool includeAssignments, CancellationToken cancellationToken)
    {
        var inventory = await new InventoryQueryService(db).GetLocationsInventoryAsync(locationIds, cancellationToken);
        return inventory.Where(item => includeAssignments || item.HasNonZeroBalance).GroupBy(item => item.LocationId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DisplayProduct>)group
                .OrderBy(item => item.ProductSku, StringComparer.Ordinal)
                .Select(item => new DisplayProduct(item.ProductSku, item.ProductDescription, item.Quantity, item.UnitCode,
                    item.ProductId, item.HasActiveAssignment, item.HasNonZeroBalance, item.ProductIsActive)).ToArray());
    }

    private async Task<string> UpdatedAtAsync(CancellationToken cancellationToken) =>
        Text("Actualizado {0}", (await clock.ConvertAsync(DateTimeOffset.UtcNow, cancellationToken))
            .ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture));
}
