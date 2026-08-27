using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Catalogs;

public sealed record ProductCatalogFilter(string? Search, string Status = "active", string Stock = "all", string Assignment = "all", short? UnitId = null, short? TypeId = null, short? ClassId = null);
public sealed record ProductCatalogSummary(int Active, int Inactive, int WithBalance, int Negative, int BelowMinimum, int WithoutAssignment);
public sealed record ProductCatalogRow(Guid Id, string Sku, string? Description, string? Reference, string Unit, string? Type, string? Class, decimal Quantity, decimal Minimum, int Locations, bool IsActive, bool IsNegative, bool IsBelowMinimum, bool HasAssignment);
public sealed record ProductCatalogPage(ProductCatalogSummary Summary, IReadOnlyList<ProductCatalogRow> Items, int TotalCount);
public sealed record ProductCatalogLocation(Guid Id, string Code, string? Description, decimal Quantity, bool IsAssigned, bool IsActive, bool IsBlocked);
public sealed record ProductCatalogLot(Guid Id, string Number, DateOnly? Date, decimal Quantity);
public sealed record ProductCatalogMovement(Guid Id, DateTimeOffset OccurredAt, string Type, decimal Quantity, string Route, string Responsible);
public sealed record ProductCatalogDetail(Guid Id, string Sku, string? Description, string? Reference, string Unit, string? Type, string? Class, bool IsActive, decimal Quantity, decimal Minimum, int LocationsWithBalance, int ActiveAssignments, int NegativePositions, IReadOnlyList<ProductCatalogLocation> Locations, IReadOnlyList<ProductCatalogLot> Lots, IReadOnlyList<ProductCatalogMovement> Movements);

public sealed class ProductCatalogQueryService(WarehouseDbContext db, WarehouseSettingsService settings)
{
    public async Task<ProductCatalogPage> SearchAsync(ProductCatalogFilter filter, int page, int pageSize, CancellationToken token = default)
    {
        var products = db.Products.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            products = products.Where(p => p.Sku.ToUpper().Contains(term) || (p.Description != null && p.Description.ToUpper().Contains(term)) || (p.ExternalReference != null && p.ExternalReference.ToUpper().Contains(term)) || p.Barcodes.Any(b => b.Barcode.ToUpper().Contains(term)) || p.LocationAssignments.Any(a => a.IsActive && a.Location.Code.ToUpper().Contains(term)));
        }
        if (filter.UnitId is not null) products = products.Where(p => p.BaseUnitId == filter.UnitId);
        if (filter.TypeId is not null) products = products.Where(p => p.ProductTypeId == filter.TypeId);
        if (filter.ClassId is not null) products = products.Where(p => p.ProductClassId == filter.ClassId);

        var baseRows = await products.Select(p => new { p.Id, p.Sku, p.Description, Reference = p.ExternalReference, Unit = p.BaseUnit.Code, Type = p.ProductType == null ? null : p.ProductType.Code, Class = p.ProductClass == null ? null : p.ProductClass.Code, p.MinimumStock, p.IsActive, HasAssignment = p.LocationAssignments.Any(a => a.IsActive) }).ToListAsync(token);
        var ids = baseRows.Select(p => p.Id).ToArray();
        var balances = ids.Length == 0 ? [] : await db.InventoryBalances.AsNoTracking().Where(b => ids.Contains(b.ProductId)).GroupBy(b => b.ProductId).Select(g => new { ProductId = g.Key, Quantity = g.Sum(b => b.Quantity), Locations = g.Where(b => b.Quantity != 0).Select(b => b.LocationId).Distinct().Count(), Negative = g.Any(b => b.Quantity < 0) }).ToListAsync(token);
        var totals = balances.ToDictionary(b => b.ProductId);
        var all = baseRows.Select(p => { var balance = totals.GetValueOrDefault(p.Id); var quantity = balance?.Quantity ?? 0m; return new ProductCatalogRow(p.Id, p.Sku, p.Description, p.Reference, p.Unit, p.Type, p.Class, quantity, p.MinimumStock, balance?.Locations ?? 0, p.IsActive, balance?.Negative ?? false, quantity < p.MinimumStock, p.HasAssignment); }).ToArray();
        var active = all.Where(p => p.IsActive).ToArray();
        var summary = new ProductCatalogSummary(
            active.Length,
            all.Count(p => !p.IsActive),
            active.Count(p => p.Quantity != 0),
            active.Count(p => p.IsNegative),
            active.Count(p => p.IsBelowMinimum),
            active.Count(p => !p.HasAssignment));
        var filtered = all
            .Where(p => filter.Status switch { "active" => p.IsActive, "inactive" => !p.IsActive, _ => true })
            .Where(p => filter.Stock switch { "balance" => p.Quantity != 0, "empty" => p.Quantity == 0, "negative" => p.IsNegative, "minimum" => p.IsBelowMinimum, _ => true })
            .Where(p => filter.Assignment switch { "assigned" => p.HasAssignment, "unassigned" => !p.HasAssignment, _ => true })
            .OrderBy(p => p.Sku, StringComparer.Ordinal)
            .ToArray();
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)safePageSize));
        var safePage = Math.Min(Math.Max(1, page), totalPages);
        return new(summary, filtered.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray(), filtered.Length);
    }

    public async Task<ProductCatalogDetail?> GetAsync(Guid id, CancellationToken token = default)
    {
        var product = await db.Products.AsNoTracking().Where(p => p.Id == id).Select(p => new { p.Id, p.Sku, p.Description, Reference = p.ExternalReference, Unit = p.BaseUnit.Code, Type = p.ProductType == null ? null : p.ProductType.Code, Class = p.ProductClass == null ? null : p.ProductClass.Code, p.IsActive, p.MinimumStock }).SingleOrDefaultAsync(token);
        if (product is null) return null;
        var assignments = await db.ProductLocationAssignments.AsNoTracking().Where(a => a.ProductId == id && a.IsActive).Select(a => new { a.LocationId, a.Location.Code, a.Location.Description, a.Location.IsActive, a.Location.IsBlocked }).ToListAsync(token);
        var balances = await db.InventoryBalances.AsNoTracking().Where(b => b.ProductId == id).GroupBy(b => new { b.LocationId, b.Location.Code, b.Location.Description, b.Location.IsActive, b.Location.IsBlocked }).Select(g => new { g.Key.LocationId, g.Key.Code, g.Key.Description, g.Key.IsActive, g.Key.IsBlocked, Quantity = g.Sum(b => b.Quantity) }).ToListAsync(token);
        var assigned = assignments.ToDictionary(a => a.LocationId);
        var locations = balances.Where(b => b.Quantity != 0).Select(b => new ProductCatalogLocation(b.LocationId, b.Code, b.Description, b.Quantity, assigned.ContainsKey(b.LocationId), b.IsActive, b.IsBlocked)).Concat(assignments.Where(a => !balances.Any(b => b.LocationId == a.LocationId && b.Quantity != 0)).Select(a => new ProductCatalogLocation(a.LocationId, a.Code, a.Description, 0m, true, a.IsActive, a.IsBlocked))).OrderBy(l => l.Code, StringComparer.Ordinal).ToArray();
        var lots = await db.ProductLots.AsNoTracking().Where(l => l.ProductId == id).Select(l => new { l.Id, l.Number, l.LotDate }).ToListAsync(token);
        var lotIds = lots.Select(l => l.Id).ToArray();
        var lotTotals = lotIds.Length == 0 ? [] : await db.InventoryBalances.AsNoTracking().Where(b => b.LotId != null && lotIds.Contains(b.LotId.Value)).GroupBy(b => b.LotId!.Value).Select(g => new { Id = g.Key, Quantity = g.Sum(b => b.Quantity) }).ToListAsync(token);
        var quantities = lotTotals.ToDictionary(l => l.Id, l => l.Quantity);
        var lotRows = lots.Select(l => new ProductCatalogLot(l.Id, l.Number, l.LotDate, quantities.GetValueOrDefault(l.Id))).OrderByDescending(l => l.Date).ThenBy(l => l.Number).ToArray();
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await settings.GetAsync(token)).TimeZoneId);
        var movements = await db.InventoryMovementLines.AsNoTracking().Where(l => l.ProductId == id).OrderByDescending(l => l.Movement.OccurredAt).Take(10).Select(l => new { l.MovementId, l.Movement.OccurredAt, Type = l.Movement.Type.ToString(), l.Quantity, Source = l.SourceLocation == null ? null : l.SourceLocation.Code, Destination = l.DestinationLocation == null ? null : l.DestinationLocation.Code, Responsible = l.Movement.ResponsibleUser.FullName }).ToListAsync(token);
        var movementRows = movements.Select(m => new ProductCatalogMovement(m.MovementId, TimeZoneInfo.ConvertTime(m.OccurredAt, zone), m.Type, m.Quantity, $"{m.Source ?? "—"} → {m.Destination ?? "—"}", m.Responsible)).ToArray();
        return new(product.Id, product.Sku, product.Description, product.Reference, product.Unit, product.Type, product.Class, product.IsActive, locations.Sum(l => l.Quantity), product.MinimumStock, locations.Count(l => l.Quantity != 0), assignments.Count, locations.Count(l => l.Quantity < 0), locations, lotRows, movementRows);
    }
}
