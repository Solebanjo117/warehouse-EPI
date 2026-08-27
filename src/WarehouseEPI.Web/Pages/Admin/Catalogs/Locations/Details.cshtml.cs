using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations;

[Authorize(Policy = "AdminOnly")]
public sealed class DetailsModel(
    WarehouseDbContext dbContext,
    ProductLocationAssignmentService assignmentService) : PageModel
{
    public LocationDetails Location { get; private set; } = null!;
    public IReadOnlyList<AssignmentRow> Assignments { get; private set; } = [];
    public IReadOnlyList<ProductResult> ProductResults { get; private set; } = [];
    public IReadOnlyList<BalanceRow> Balances { get; private set; } = [];
    public IReadOnlyList<MovementRow> RecentMovements { get; private set; } = [];
    public IReadOnlyList<NeighborRow> Neighbors { get; private set; } = [];
    public bool IsMapped { get; private set; }
    public int ActiveAssignmentCount => Assignments.Count(item => item.IsActive);
    public int BalanceProductCount => Balances.Select(item => item.ProductId).Distinct().Count();
    public string? ProductSearch { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, string? productSearch, CancellationToken cancellationToken)
    {
        ProductSearch = productSearch?.Trim();
        if (!await LoadAsync(id, cancellationToken)) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(Guid id, Guid productId, CancellationToken cancellationToken)
    {
        var result = await assignmentService.AssignAsync(productId, id, cancellationToken);
        SetResultMessage(result);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeactivateAsync(Guid id, Guid productId, CancellationToken cancellationToken)
    {
        var result = await assignmentService.DeactivateAsync(productId, id, cancellationToken);
        if (result == ProductLocationAssignmentResult.Success) Message = "La asignación fue desactivada.";
        else Error = "La asignación activa ya no existe.";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var location = await dbContext.Locations.AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new LocationDetails(candidate.Id, candidate.Code, candidate.Kind,
                candidate.RowCode, candidate.RackNumber, candidate.PalletNumber, candidate.Description, candidate.OperationalRole,
                candidate.IsActive, candidate.IsBlocked, candidate.BlockReason,
                candidate.IsPhysicallyPresent))
            .SingleOrDefaultAsync(cancellationToken);
        if (location is null) return false;
        Location = location;

        Assignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(assignment => assignment.LocationId == id)
            .OrderByDescending(assignment => assignment.IsActive).ThenBy(assignment => assignment.Product.Sku)
            .Select(assignment => new AssignmentRow(assignment.ProductId, assignment.Product.Sku,
                assignment.Product.Description, assignment.Product.IsActive, assignment.IsActive))
            .ToListAsync(cancellationToken);

        var rackPositions = location.Kind == LocationKind.Rack
            ? await dbContext.Locations.AsNoTracking()
                .Where(candidate => candidate.RowCode == location.RowCode &&
                    candidate.RackNumber == location.RackNumber && candidate.IsPhysicallyPresent)
                .OrderBy(candidate => candidate.PalletNumber)
                .Select(candidate => new NeighborBaseRow(candidate.Id, candidate.Code, candidate.PalletNumber,
                    candidate.IsActive, candidate.IsBlocked, candidate.BlockReason))
                .ToListAsync(cancellationToken)
            : [new NeighborBaseRow(location.Id, location.Code, location.PalletNumber,
                location.IsActive, location.IsBlocked, location.BlockReason)];
        var rackLocationIds = rackPositions.Select(item => item.Id).ToArray();
        var activeAssignments = await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(assignment => assignment.IsActive && rackLocationIds.Contains(assignment.LocationId))
            .OrderBy(assignment => assignment.Product.Sku)
            .Select(assignment => new AssignmentSource(assignment.LocationId, assignment.ProductId, assignment.Product.Sku))
            .ToListAsync(cancellationToken);
        var assignmentsByLocation = activeAssignments.GroupBy(item => item.LocationId)
            .ToDictionary(group => group.Key, group => group.ToArray() as IReadOnlyList<AssignmentSource>);
        var assignedKeys = activeAssignments.Select(item => (item.LocationId, item.ProductId)).ToHashSet();
        var rackBalanceSources = await dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => rackLocationIds.Contains(balance.LocationId))
            .Select(balance => new BalanceSource(balance.LocationId, balance.ProductId, balance.Product.Sku,
                balance.Product.Description, balance.Product.BaseUnit.Code, balance.Quantity))
            .ToListAsync(cancellationToken);
        var balancesByLocation = rackBalanceSources.GroupBy(balance => balance.LocationId)
            .ToDictionary(locationGroup => locationGroup.Key,
                locationGroup => locationGroup
                    .GroupBy(balance => new { balance.ProductId, balance.Sku, balance.Description, balance.Unit })
                    .Select(group => new BalanceRow(group.Key.ProductId, group.Key.Sku, group.Key.Description,
                        group.Key.Unit, group.Sum(balance => balance.Quantity),
                        assignedKeys.Contains((locationGroup.Key, group.Key.ProductId))))
                    .Where(item => item.Quantity != 0)
                    .OrderBy(item => item.Sku, StringComparer.Ordinal)
                    .ToArray() as IReadOnlyList<BalanceRow>);
        Balances = balancesByLocation.GetValueOrDefault(id) ?? [];
        if (location.Kind == LocationKind.Rack)
            Neighbors = rackPositions.Select(item =>
            {
                var assignments = assignmentsByLocation.GetValueOrDefault(item.Id) ?? [];
                return new NeighborRow(item.Id, item.Code, item.PalletNumber, item.Id == id, item.IsActive,
                    item.IsBlocked, item.BlockReason, balancesByLocation.GetValueOrDefault(item.Id) ?? [],
                    assignments.Select(assignment => assignment.Sku).ToArray(), assignments.Count);
            }).ToArray();
        var locationChanges = await dbContext.InventoryBalanceChanges.AsNoTracking().Where(change => change.LocationId == id)
            .Select(change => new MovementSource(change.MovementLine.MovementId, change.MovementLine.Movement.OccurredAt,
                change.MovementLine.Movement.Type, change.MovementLine.Product.Sku, change.DeltaQuantity))
            .ToListAsync(cancellationToken);
        RecentMovements = locationChanges.GroupBy(change => new { change.MovementId, change.OccurredAt, change.Type, change.Sku })
            .Select(group => new MovementRow(group.Key.MovementId, group.Key.OccurredAt, group.Key.Type, group.Key.Sku,
                group.Sum(change => change.Delta)))
            .OrderByDescending(item => item.OccurredAt).Take(10).ToArray();
        IsMapped = await dbContext.WarehouseMapElements.AsNoTracking().AnyAsync(item => item.IsVisible && (item.LocationId == id || (item.RowCode == location.RowCode && item.RackNumber == location.RackNumber)), cancellationToken);

        if (!string.IsNullOrWhiteSpace(ProductSearch))
        {
            var term = ProductSearch.ToUpperInvariant();
            ProductResults = await dbContext.Products.AsNoTracking()
                .Where(product => product.IsActive &&
                    (product.Sku.Contains(term) ||
                     (product.Description != null && product.Description.ToUpper().Contains(term)) ||
                     (product.ExternalReference != null && product.ExternalReference.ToUpper().Contains(term)) ||
                     product.Barcodes.Any(barcode => barcode.IsActive && barcode.Barcode.ToUpper().Contains(term))))
                .OrderBy(product => product.Sku).Take(20)
                .Select(product => new ProductResult(product.Id, product.Sku, product.Description,
                    product.LocationAssignments.Any(assignment => assignment.LocationId == id && assignment.IsActive)))
                .ToListAsync(cancellationToken);
        }
        return true;
    }

    private void SetResultMessage(ProductLocationAssignmentResult result)
    {
        var messages = result switch
        {
            ProductLocationAssignmentResult.Success => (Success: "Producto asignado a la ubicación.", Error: (string?)null),
            ProductLocationAssignmentResult.AlreadyActive => (Success: (string?)null, Error: "El producto ya está asignado a esta ubicación."),
            ProductLocationAssignmentResult.ProductInactive => (Success: (string?)null, Error: "No se puede asignar un producto inactivo."),
            ProductLocationAssignmentResult.LocationInactive => (Success: (string?)null, Error: "No se puede asignar a una ubicación inactiva."),
            ProductLocationAssignmentResult.LocationBlocked => (Success: (string?)null, Error: "No se puede asignar a una ubicación bloqueada."),
            ProductLocationAssignmentResult.LocationDoesNotTrackInventory => (Success: (string?)null, Error: "Los racks WIP no admiten asignaciones ni saldo."),
            _ => (Success: (string?)null, Error: "El producto o la ubicación ya no existe.")
        };
        Message = messages.Success;
        Error = messages.Error;
    }

    public sealed record LocationDetails(Guid Id, string Code, LocationKind Kind, string? RowCode,
        short? RackNumber, short? PalletNumber, string? Description, LocationOperationalRole OperationalRole, bool IsActive,
        bool IsBlocked, string? BlockReason, bool IsPhysicallyPresent);
    public sealed record AssignmentRow(Guid ProductId, string Sku, string? Description,
        bool ProductIsActive, bool IsActive);
    public sealed record ProductResult(Guid Id, string Sku, string? Description, bool IsAssigned);
    private sealed record NeighborBaseRow(Guid Id, string Code, short? PalletNumber, bool IsActive,
        bool IsBlocked, string? BlockReason);
    private sealed record AssignmentSource(Guid LocationId, Guid ProductId, string Sku);
    private sealed record BalanceSource(Guid LocationId, Guid ProductId, string Sku, string? Description,
        string Unit, decimal Quantity);
    private sealed record MovementSource(Guid MovementId, DateTimeOffset OccurredAt, InventoryMovementType Type, string Sku, decimal Delta);
    public sealed record BalanceRow(Guid ProductId, string Sku, string? Description, string Unit, decimal Quantity, bool IsAssigned);
    public sealed record MovementRow(Guid MovementId, DateTimeOffset OccurredAt, InventoryMovementType Type, string Sku, decimal Delta);
    public sealed record NeighborRow(Guid Id, string Code, short? PalletNumber, bool IsCurrent, bool IsActive,
        bool IsBlocked, string? BlockReason, IReadOnlyList<BalanceRow> Balances,
        IReadOnlyList<string> AssignedSkus, int AssignmentCount)
    {
        public bool HasInventory => Balances.Count > 0;
        public bool HasNegative => Balances.Any(item => item.Quantity < 0);
        public bool HasUnassignedBalance => Balances.Any(item => !item.IsAssigned);
        public BalanceRow? PrimaryBalance => Balances.FirstOrDefault();
        public string? PrimaryAssignedSku => AssignedSkus.FirstOrDefault();
        public int AdditionalProductCount => Math.Max(0, Balances.Count - 1);
        public int AdditionalAssignmentCount => HasInventory ? 0 : Math.Max(0, AssignmentCount - 1);
        public string InventoryState => HasNegative ? "Saldo negativo"
            : HasUnassignedBalance ? "Saldo sin asignación"
            : HasInventory ? "Con saldo"
            : AssignmentCount > 0 ? "Asignado sin saldo"
            : "Sin producto";
        public string InventoryClass => HasNegative ? "negative"
            : HasUnassignedBalance ? "unassigned"
            : HasInventory ? "occupied"
            : AssignmentCount > 0 ? "assigned-empty"
            : "empty";
        public string OperationalState => !IsActive ? "Inactiva" : IsBlocked ? "Bloqueada" : "Activa";
    }
}
