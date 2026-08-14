using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record InventoryHistoryFilter(DateTimeOffset? From, DateTimeOffset? To, InventoryMovementType? Type, string? Search, Guid? ProductId, Guid? LocationId, Guid? ResponsibleUserId);
public sealed record InventoryMovementHistoryRow(Guid Id, Guid OperationId, InventoryMovementType Type, DateTimeOffset OccurredAt, string ResponsibleName, string? Reference, string ProductSummary, bool IsCorrected, Guid? CorrectionId);
public sealed record InventoryMovementHistoryPage(IReadOnlyList<InventoryMovementHistoryRow> Items, int TotalCount);
public sealed record InventoryMovementDetailLine(Guid ProductId, string Sku, string? Description, string Unit, decimal Quantity, string? Source, string? Destination, decimal? Previous, decimal? AdjustmentDelta, IReadOnlyList<InventoryReceiptChange> Changes);
public sealed record InventoryMovementDetail(Guid Id, Guid OperationId, InventoryMovementType Type, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, string ResponsibleName, string? Reference, string? Notes, IReadOnlyList<InventoryMovementDetailLine> Lines, InventoryCorrectionLink? OriginalCorrection, InventoryCorrectionLink? ReversalCorrection, InventoryCorrectionLink? ReplacementCorrection);
public sealed record InventoryCorrectionLink(Guid CorrectionId, InventoryMovementCorrectionType Type, Guid OriginalMovementId, Guid ReversalMovementId, Guid? ReplacementMovementId, string Reason, string RequestedBy, string AuthorizedBy);

public sealed class InventoryHistoryService(WarehouseDbContext dbContext)
{
    public async Task<InventoryMovementHistoryPage> SearchAsync(InventoryHistoryFilter filter, int page, int pageSize, CancellationToken token = default)
    {
        var query = Apply(dbContext.InventoryMovements.AsNoTracking(), filter);
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id)
            .Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize)
            .Select(item => new InventoryMovementHistoryRow(item.Id, item.OperationId, item.Type, item.OccurredAt, item.ResponsibleUser.FullName, item.Reference,
                item.Lines.OrderBy(line => line.LineNumber).Select(line => line.Product.Sku).FirstOrDefault() ?? "—",
                dbContext.InventoryMovementCorrections.Any(c => c.OriginalMovementId == item.Id || c.ReversalMovementId == item.Id || c.ReplacementMovementId == item.Id),
                dbContext.InventoryMovementCorrections.Where(c => c.OriginalMovementId == item.Id || c.ReversalMovementId == item.Id || c.ReplacementMovementId == item.Id).Select(c => (Guid?)c.Id).FirstOrDefault()))
            .ToListAsync(token);
        return new(rows, total);
    }

    public async Task<IReadOnlyList<InventoryMovementHistoryRow>> ExportAsync(InventoryHistoryFilter filter, CancellationToken token = default)
    {
        var query = Apply(dbContext.InventoryMovements.AsNoTracking(), filter);
        return await query.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id)
            .Select(item => new InventoryMovementHistoryRow(item.Id, item.OperationId, item.Type, item.OccurredAt, item.ResponsibleUser.FullName, item.Reference,
                item.Lines.OrderBy(line => line.LineNumber).Select(line => line.Product.Sku).FirstOrDefault() ?? "—", false, null)).ToListAsync(token);
    }

    public async Task<InventoryMovementDetail?> GetAsync(Guid id, CancellationToken token = default)
    {
        var item = await dbContext.InventoryMovements.AsNoTracking().Include(m => m.ResponsibleUser)
            .Include(m => m.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.BaseUnit)
            .Include(m => m.Lines).ThenInclude(l => l.SourceLocation)
            .Include(m => m.Lines).ThenInclude(l => l.DestinationLocation)
            .Include(m => m.Lines).ThenInclude(l => l.BalanceChanges).ThenInclude(c => c.Location)
            .SingleOrDefaultAsync(m => m.Id == id, token);
        if (item is null) return null;
        var correction = await dbContext.InventoryMovementCorrections.AsNoTracking().Include(c => c.RequestedByUser).Include(c => c.AuthorizedByUser)
            .SingleOrDefaultAsync(c => c.OriginalMovementId == id || c.ReversalMovementId == id || c.ReplacementMovementId == id, token);
        InventoryCorrectionLink? link = correction is null ? null : new(correction.Id, correction.Type, correction.OriginalMovementId, correction.ReversalMovementId, correction.ReplacementMovementId, correction.Reason, correction.RequestedByUser.FullName, correction.AuthorizedByUser.FullName);
        return new(item.Id, item.OperationId, item.Type, item.OccurredAt, item.RecordedAt, item.ResponsibleUser.FullName, item.Reference, item.Notes,
            item.Lines.OrderBy(l => l.LineNumber).Select(l => new InventoryMovementDetailLine(l.ProductId, l.Product.Sku, l.Product.Description, l.Product.BaseUnit.Code, l.Quantity, l.SourceLocation?.Code, l.DestinationLocation?.Code, l.PreviousQuantity, l.AdjustmentDelta,
                l.BalanceChanges.OrderBy(c => c.Location.Code).Select(c => new InventoryReceiptChange(c.Location.Code, c.PreviousQuantity, c.DeltaQuantity, c.ResultingQuantity)).ToArray())).ToArray(),
            correction?.OriginalMovementId == id ? link : null, correction?.ReversalMovementId == id ? link : null, correction?.ReplacementMovementId == id ? link : null);
    }

    private static IQueryable<InventoryMovement> Apply(IQueryable<InventoryMovement> query, InventoryHistoryFilter f)
    {
        if (f.From is not null) query = query.Where(m => m.OccurredAt >= f.From);
        if (f.To is not null) query = query.Where(m => m.OccurredAt < f.To.Value.AddDays(1));
        if (f.Type is not null) query = query.Where(m => m.Type == f.Type);
        if (f.ResponsibleUserId is not null) query = query.Where(m => m.ResponsibleUserId == f.ResponsibleUserId);
        if (f.ProductId is not null) query = query.Where(m => m.Lines.Any(l => l.ProductId == f.ProductId));
        if (f.LocationId is not null) query = query.Where(m => m.Lines.Any(l => l.SourceLocationId == f.LocationId || l.DestinationLocationId == f.LocationId || l.BalanceChanges.Any(c => c.LocationId == f.LocationId)));
        if (!string.IsNullOrWhiteSpace(f.Search)) { var term = f.Search.Trim(); query = query.Where(m => m.Id.ToString().Contains(term) || m.OperationId.ToString().Contains(term) || (m.Reference != null && m.Reference.Contains(term)) || m.Lines.Any(l => l.Product.Sku.Contains(term))); }
        return query;
    }
}
