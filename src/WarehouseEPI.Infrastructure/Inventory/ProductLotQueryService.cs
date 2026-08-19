using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

public enum LotBalanceFilter { All, WithBalance, Positive, Negative, Exhausted }
public enum LotDateFilter { All, WithDate, WithoutDate }
public sealed record ProductLotQueryFilter(string? Search, LotBalanceFilter Balance = LotBalanceFilter.All, LotDateFilter Date = LotDateFilter.All, DateOnly? From = null, DateOnly? To = null);
public sealed record ProductLotRow(Guid Id, Guid ProductId, string Sku, string? Description, string Number, DateOnly? LotDate, DateTimeOffset CreatedAt, string Unit, decimal Quantity, int Locations);
public sealed record ProductLotPage(IReadOnlyList<ProductLotRow> Items, int TotalCount);
public sealed record ProductLotLocationBalance(Guid LocationId, string Code, string? Description, decimal Quantity, string Unit);
public sealed record ProductLotMovement(Guid MovementId, DateTimeOffset OccurredAt, string Type, string Location, decimal Delta, decimal Previous, decimal Resulting);
public sealed record ProductLotDateAudit(Guid Id, DateOnly? Previous, DateOnly? Current, string Reason, string RequestedBy, string AuthorizedBy, DateTimeOffset RecordedAt);
public sealed record ProductLotDetail(Guid Id, Guid ProductId, string Sku, string? Description, string Number, DateOnly? LotDate, DateTimeOffset CreatedAt, string Unit, decimal Quantity, IReadOnlyList<ProductLotLocationBalance> Locations, IReadOnlyList<ProductLotMovement> Movements, IReadOnlyList<ProductLotDateAudit> DateChanges);

public sealed class ProductLotQueryService(WarehouseDbContext dbContext)
{
    public async Task<ProductLotPage> SearchAsync(ProductLotQueryFilter filter, int page, int pageSize, CancellationToken token = default)
    {
        var lots = dbContext.ProductLots.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            lots = lots.Where(l => l.NormalizedNumber.Contains(term) || l.Product.Sku.Contains(term) || (l.Product.Description != null && l.Product.Description.Contains(term)) || (l.Product.ExternalReference != null && l.Product.ExternalReference.Contains(term)) || l.Product.Barcodes.Any(b => b.Barcode.ToUpper().Contains(term)));
        }
        if (filter.Date == LotDateFilter.WithDate) lots = lots.Where(l => l.LotDate != null);
        if (filter.Date == LotDateFilter.WithoutDate) lots = lots.Where(l => l.LotDate == null);
        if (filter.From is not null) lots = lots.Where(l => l.LotDate >= filter.From);
        if (filter.To is not null) lots = lots.Where(l => l.LotDate <= filter.To);
        var candidates = await lots.Select(l => new { l.Id, l.ProductId, l.Product.Sku, l.Product.Description, l.Number, l.LotDate, l.CreatedAt, Unit = l.Product.BaseUnit.Code }).ToListAsync(token);
        var ids = candidates.Select(l => l.Id).ToArray();
        var totals = await dbContext.InventoryBalances.AsNoTracking().Where(b => b.LotId != null && ids.Contains(b.LotId.Value))
            .GroupBy(b => b.LotId!.Value).Select(g => new { LotId = g.Key, Quantity = g.Sum(b => b.Quantity), Locations = g.Where(b => b.Quantity != 0).Select(b => b.LocationId).Distinct().Count() }).ToDictionaryAsync(x => x.LotId, token);
        var projected = candidates.Select(l => { var total = totals.GetValueOrDefault(l.Id); return new ProductLotRow(l.Id, l.ProductId, l.Sku, l.Description, l.Number, l.LotDate, l.CreatedAt, l.Unit, total?.Quantity ?? 0m, total?.Locations ?? 0); });
        projected = filter.Balance switch { LotBalanceFilter.WithBalance => projected.Where(l => l.Quantity != 0), LotBalanceFilter.Positive => projected.Where(l => l.Quantity > 0), LotBalanceFilter.Negative => projected.Where(l => l.Quantity < 0), LotBalanceFilter.Exhausted => projected.Where(l => l.Quantity == 0), _ => projected };
        var ordered = projected.OrderByDescending(l => l.LotDate).ThenBy(l => l.Sku).ThenBy(l => l.Number).ToArray();
        var totalCount = ordered.Length;
        var items = ordered.Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToArray();
        return new(items, totalCount);
    }

    public async Task<ProductLotDetail?> GetAsync(Guid id, CancellationToken token = default)
    {
        var lot = await dbContext.ProductLots.AsNoTracking().Include(l => l.Product).ThenInclude(p => p.BaseUnit).SingleOrDefaultAsync(l => l.Id == id, token);
        if (lot is null) return null;
        var locations = await dbContext.InventoryBalances.AsNoTracking().Where(b => b.LotId == id && b.Quantity != 0).OrderBy(b => b.Location.Code)
            .Select(b => new ProductLotLocationBalance(b.LocationId, b.Location.Code, b.Location.Description, b.Quantity, lot.Product.BaseUnit.Code)).ToListAsync(token);
        var movements = await dbContext.InventoryBalanceChanges.AsNoTracking().Where(c => c.LotId == id || (c.LotNumberSnapshot == lot.Number && c.MovementLine.ProductId == lot.ProductId)).OrderByDescending(c => c.MovementLine.Movement.OccurredAt)
            .Select(c => new ProductLotMovement(c.MovementLine.MovementId, c.MovementLine.Movement.OccurredAt, c.MovementLine.Movement.Type.ToString(), c.Location.Code, c.DeltaQuantity, c.PreviousQuantity, c.ResultingQuantity)).ToListAsync(token);
        var audits = await dbContext.ProductLotDateChanges.AsNoTracking().Where(c => c.ProductLotId == id).OrderByDescending(c => c.RecordedAt)
            .Select(c => new ProductLotDateAudit(c.Id, c.PreviousLotDate, c.NewLotDate, c.Reason, c.RequestedByUser.FullName, c.AuthorizedByUser.FullName, c.RecordedAt)).ToListAsync(token);
        return new(lot.Id, lot.ProductId, lot.Product.Sku, lot.Product.Description, lot.Number, lot.LotDate, lot.CreatedAt, lot.Product.BaseUnit.Code, locations.Sum(l => l.Quantity), locations, movements, audits);
    }
}
