using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

public enum InventoryHistoryCorrectionState { All, Current, CorrectedOriginal, Reversal, Replacement }
public sealed record InventoryHistoryFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    InventoryMovementType? Type,
    string? Search,
    Guid? ProductId,
    Guid? LocationId,
    Guid? ResponsibleUserId,
    InventoryHistoryCorrectionState State = InventoryHistoryCorrectionState.All,
    InventoryMovementPurpose? Purpose = null,
    string? ProductSearch = null,
    string? LocationSearch = null);
public sealed record InventoryMovementHistoryRow(Guid Id, Guid OperationId, InventoryMovementType Type, InventoryMovementPurpose Purpose, string? OperationalArea, DateTimeOffset OccurredAt, string ResponsibleName, string? Reference, string ProductSummary, bool IsCorrected, Guid? CorrectionId, string Status, string Route, string Quantity, bool HasNegativeBalance, string? LotSummary);
public sealed record InventoryMovementHistoryPage(IReadOnlyList<InventoryMovementHistoryRow> Items, int TotalCount);
public sealed record InventoryMovementBalanceChange(Guid LocationId, string Location, Guid? LotId, string? LotNumber, DateOnly? LotDate, decimal Previous, decimal Delta, decimal Resulting);
public sealed record InventoryMovementDetailLine(Guid ProductId, string Sku, string? Description, string Unit, decimal Quantity, Guid? SourceLocationId, string? Source, Guid? DestinationLocationId, string? Destination, Guid? LotId, string? LotNumber, DateOnly? LotDate, decimal? Previous, decimal? AdjustmentDelta, IReadOnlyList<InventoryMovementBalanceChange> Changes);
public sealed record InventoryMovementDetail(Guid Id, Guid OperationId, InventoryMovementType Type, InventoryMovementPurpose Purpose, string? OperationalArea, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, string ResponsibleName, string? Reference, string? Notes, IReadOnlyList<InventoryMovementDetailLine> Lines, InventoryCorrectionLink? OriginalCorrection, InventoryCorrectionLink? ReversalCorrection, InventoryCorrectionLink? ReplacementCorrection);
public sealed record InventoryCorrectionLink(Guid CorrectionId, InventoryMovementCorrectionType Type, Guid OriginalMovementId, Guid ReversalMovementId, Guid? ReplacementMovementId, string Reason, string RequestedBy, string AuthorizedBy);
public sealed record InventoryMovementTraceRow(Guid MovementId, Guid OperationId, InventoryMovementType Type, InventoryMovementPurpose Purpose, string Status, DateTimeOffset OccurredAt, string TimeZoneId, string Responsible, string? Reference, string? Notes, string ProductSku, string? ProductDescription, string Unit, decimal CapturedQuantity, string? Source, string? Destination, string? OperationalArea, string? Location, string? LotNumber, DateOnly? LotDate, string AllocationMode, decimal? Previous, decimal? Delta, decimal? Resulting);
public sealed record InventoryMovementTraceExportBatch(IReadOnlyList<InventoryMovementTraceRow> Items, int TotalOperations, int TotalRows, int MaximumRows)
{
    public bool ExceedsLimit => TotalRows > MaximumRows;
}

public sealed class InventoryHistoryService(WarehouseDbContext dbContext)
{
    public async Task<InventoryMovementHistoryPage> SearchAsync(InventoryHistoryFilter filter, int page, int pageSize, CancellationToken token = default)
    {
        var query = Apply(dbContext.InventoryMovements.AsNoTracking(), filter);
        var total = await query.CountAsync(token);
        var items = await query.Include(m => m.ResponsibleUser).Include(m => m.OperationalArea).Include(m => m.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.BaseUnit)
            .Include(m => m.Lines).ThenInclude(l => l.SourceLocation).Include(m => m.Lines).ThenInclude(l => l.DestinationLocation).Include(m => m.Lines).ThenInclude(l => l.Lot)
            .Include(m => m.Lines).ThenInclude(l => l.BalanceChanges).OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Id)
            .Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var links = await GetCorrectionStatesAsync(items.Select(m => m.Id), token);
        return new(items.Select(m => ToRow(m, links.GetValueOrDefault(m.Id))).ToArray(), total);
    }

    public async Task<InventoryMovementTraceExportBatch> GetTraceExportAsync(InventoryHistoryFilter filter, string timeZoneId, int maximumRows = 10000, CancellationToken token = default)
    {
        var limit = Math.Clamp(maximumRows, 1, 50000);
        var query = Apply(dbContext.InventoryMovements.AsNoTracking(), filter);
        var totalOperations = await query.CountAsync(token);
        var totalRows = await query
            .SelectMany(movement => movement.Lines)
            .Select(line => (int?)(line.BalanceChanges.Count == 0 ? 1 : line.BalanceChanges.Count))
            .SumAsync(token) ?? 0;
        if (totalRows > limit)
            return new([], totalOperations, totalRows, limit);

        var movements = await query.Include(m => m.ResponsibleUser).Include(m => m.OperationalArea).Include(m => m.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.BaseUnit)
            .Include(m => m.Lines).ThenInclude(l => l.SourceLocation).Include(m => m.Lines).ThenInclude(l => l.DestinationLocation).Include(m => m.Lines).ThenInclude(l => l.Lot)
            .Include(m => m.Lines).ThenInclude(l => l.BalanceChanges).ThenInclude(c => c.Location).OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Id).ToListAsync(token);
        var states = await GetCorrectionStatesAsync(movements.Select(m => m.Id), token);
        var rows = new List<InventoryMovementTraceRow>();
        foreach (var movement in movements) foreach (var line in movement.Lines.OrderBy(l => l.LineNumber))
        {
            var changes = line.BalanceChanges.Count != 0 ? line.BalanceChanges.Select(c => (InventoryBalanceChange?)c) : [null];
            foreach (var change in changes) rows.Add(new(movement.Id, movement.OperationId, movement.Type, movement.Purpose, states.GetValueOrDefault(movement.Id, "Sin relación con corrección"), movement.OccurredAt, timeZoneId, movement.ResponsibleUser.FullName, movement.Reference, movement.Notes, line.Product.Sku, line.Product.Description, line.Product.BaseUnit.Code, line.Quantity, line.SourceLocation?.Code, line.DestinationLocation?.Code, movement.OperationalArea?.Code, change?.Location.Code, change?.LotNumberSnapshot ?? line.Lot?.Number, change?.LotDateSnapshot ?? line.Lot?.LotDate, line.LotAllocationMode.ToString(), change?.PreviousQuantity, change?.DeltaQuantity, change?.ResultingQuantity));
        }
        return new(rows, totalOperations, totalRows, limit);
    }

    public async Task<InventoryMovementDetail?> GetAsync(Guid id, CancellationToken token = default)
    {
        var item = await dbContext.InventoryMovements.AsNoTracking().Include(m => m.ResponsibleUser).Include(m => m.OperationalArea).Include(m => m.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.BaseUnit)
            .Include(m => m.Lines).ThenInclude(l => l.SourceLocation).Include(m => m.Lines).ThenInclude(l => l.DestinationLocation).Include(m => m.Lines).ThenInclude(l => l.Lot)
            .Include(m => m.Lines).ThenInclude(l => l.BalanceChanges).ThenInclude(c => c.Location).SingleOrDefaultAsync(m => m.Id == id, token);
        if (item is null) return null;
        var correction = await dbContext.InventoryMovementCorrections.AsNoTracking().Include(c => c.RequestedByUser).Include(c => c.AuthorizedByUser).SingleOrDefaultAsync(c => c.OriginalMovementId == id || c.ReversalMovementId == id || c.ReplacementMovementId == id, token);
        InventoryCorrectionLink? link = correction is null ? null : new(correction.Id, correction.Type, correction.OriginalMovementId, correction.ReversalMovementId, correction.ReplacementMovementId, correction.Reason, correction.RequestedByUser.FullName, correction.AuthorizedByUser.FullName);
        return new(item.Id, item.OperationId, item.Type, item.Purpose, item.OperationalArea?.Code, item.OccurredAt, item.RecordedAt, item.ResponsibleUser.FullName, item.Reference, item.Notes, item.Lines.OrderBy(l => l.LineNumber).Select(l => new InventoryMovementDetailLine(l.ProductId, l.Product.Sku, l.Product.Description, l.Product.BaseUnit.Code, l.Quantity, l.SourceLocationId, l.SourceLocation?.Code, l.DestinationLocationId, l.DestinationLocation?.Code, l.LotId, l.Lot?.Number, l.Lot?.LotDate, l.PreviousQuantity, l.AdjustmentDelta, l.BalanceChanges.OrderBy(c => c.Location.Code).Select(c => new InventoryMovementBalanceChange(c.LocationId, c.Location.Code, c.LotId, c.LotNumberSnapshot, c.LotDateSnapshot, c.PreviousQuantity, c.DeltaQuantity, c.ResultingQuantity)).ToArray())).ToArray(), correction?.OriginalMovementId == id ? link : null, correction?.ReversalMovementId == id ? link : null, correction?.ReplacementMovementId == id ? link : null);
    }

    private InventoryMovementHistoryRow ToRow(InventoryMovement movement, string? state)
    {
        var line = movement.Lines.OrderBy(l => l.LineNumber).FirstOrDefault();
        var route = line is null ? "—" : string.Join(" → ", new[] { line.SourceLocation?.Code, line.DestinationLocation?.Code ?? movement.OperationalArea?.Code }.Where(x => !string.IsNullOrWhiteSpace(x)).DefaultIfEmpty("Ubicación"));
        return new(movement.Id, movement.OperationId, movement.Type, movement.Purpose, movement.OperationalArea?.Code, movement.OccurredAt, movement.ResponsibleUser.FullName, movement.Reference, line?.Product.Sku ?? "—", state is not null, state is null ? null : dbContext.InventoryMovementCorrections.Where(c => c.OriginalMovementId == movement.Id || c.ReversalMovementId == movement.Id || c.ReplacementMovementId == movement.Id).Select(c => (Guid?)c.Id).FirstOrDefault(), state ?? "Sin relación con corrección", route, line is null ? "—" : $"{line.Quantity} {line.Product.BaseUnit.Code}", movement.Lines.SelectMany(l => l.BalanceChanges).Any(c => c.ResultingQuantity < 0), line?.Lot?.Number);
    }

    private async Task<Dictionary<Guid, string>> GetCorrectionStatesAsync(IEnumerable<Guid> ids, CancellationToken token)
    {
        var values = ids.ToArray(); var result = new Dictionary<Guid, string>();
        var corrections = await dbContext.InventoryMovementCorrections.AsNoTracking().Where(c => values.Contains(c.OriginalMovementId) || values.Contains(c.ReversalMovementId) || (c.ReplacementMovementId != null && values.Contains(c.ReplacementMovementId.Value))).ToListAsync(token);
        foreach (var c in corrections) { result[c.OriginalMovementId] = "Original corregido"; result[c.ReversalMovementId] = "Reverso"; if (c.ReplacementMovementId is not null) result[c.ReplacementMovementId.Value] = "Reemplazo"; }
        return result;
    }

    private IQueryable<InventoryMovement> Apply(IQueryable<InventoryMovement> query, InventoryHistoryFilter f)
    {
        if (f.From is not null) query = query.Where(m => m.OccurredAt >= f.From);
        if (f.To is not null) query = query.Where(m => m.OccurredAt < f.To);
        if (f.Type is not null) query = query.Where(m => m.Type == f.Type);
        if (f.Purpose is not null) query = query.Where(m => m.Purpose == f.Purpose);
        if (f.ResponsibleUserId is not null) query = query.Where(m => m.ResponsibleUserId == f.ResponsibleUserId);
        if (f.ProductId is not null) query = query.Where(m => m.Lines.Any(l => l.ProductId == f.ProductId));
        if (f.LocationId is not null) query = query.Where(m => m.Lines.Any(l => l.SourceLocationId == f.LocationId || l.DestinationLocationId == f.LocationId || l.BalanceChanges.Any(c => c.LocationId == f.LocationId)));
        if (!string.IsNullOrWhiteSpace(f.ProductSearch))
        {
            var productTerm = f.ProductSearch.Trim().ToUpperInvariant();
            query = query.Where(m => m.Lines.Any(l =>
                l.Product.Sku.ToUpper().Contains(productTerm) ||
                (l.Product.Description != null && l.Product.Description.ToUpper().Contains(productTerm)) ||
                (l.Product.ExternalReference != null && l.Product.ExternalReference.ToUpper().Contains(productTerm)) ||
                l.Product.Barcodes.Any(barcode => barcode.Barcode.ToUpper().Contains(productTerm))));
        }
        if (!string.IsNullOrWhiteSpace(f.LocationSearch))
        {
            var locationTerm = f.LocationSearch.Trim().ToUpperInvariant();
            query = query.Where(m =>
                (m.OperationalArea != null &&
                    (m.OperationalArea.Code.ToUpper().Contains(locationTerm) ||
                     (m.OperationalArea.Description != null && m.OperationalArea.Description.ToUpper().Contains(locationTerm)))) ||
                m.Lines.Any(l =>
                    (l.SourceLocation != null &&
                        (l.SourceLocation.Code.ToUpper().Contains(locationTerm) ||
                         (l.SourceLocation.Description != null && l.SourceLocation.Description.ToUpper().Contains(locationTerm)))) ||
                    (l.DestinationLocation != null &&
                        (l.DestinationLocation.Code.ToUpper().Contains(locationTerm) ||
                         (l.DestinationLocation.Description != null && l.DestinationLocation.Description.ToUpper().Contains(locationTerm)))) ||
                    l.BalanceChanges.Any(change =>
                        change.Location.Code.ToUpper().Contains(locationTerm) ||
                        (change.Location.Description != null && change.Location.Description.ToUpper().Contains(locationTerm)))));
        }
        query = f.State switch { InventoryHistoryCorrectionState.Current => query.Where(m => !dbContext.InventoryMovementCorrections.Any(c => c.OriginalMovementId == m.Id || c.ReversalMovementId == m.Id || c.ReplacementMovementId == m.Id)), InventoryHistoryCorrectionState.CorrectedOriginal => query.Where(m => dbContext.InventoryMovementCorrections.Any(c => c.OriginalMovementId == m.Id)), InventoryHistoryCorrectionState.Reversal => query.Where(m => dbContext.InventoryMovementCorrections.Any(c => c.ReversalMovementId == m.Id)), InventoryHistoryCorrectionState.Replacement => query.Where(m => dbContext.InventoryMovementCorrections.Any(c => c.ReplacementMovementId == m.Id)), _ => query };
        if (!string.IsNullOrWhiteSpace(f.Search)) { var rawTerm = f.Search.Trim(); var term = rawTerm.ToUpperInvariant(); var folioTerm = rawTerm.ToLowerInvariant(); query = query.Where(m => m.Id.ToString().Contains(folioTerm) || m.OperationId.ToString().Contains(folioTerm) || (m.Reference != null && m.Reference.ToUpper().Contains(term)) || (m.Notes != null && m.Notes.ToUpper().Contains(term)) || m.ResponsibleUser.FullName.ToUpper().Contains(term) || m.Lines.Any(l => l.Product.Sku.ToUpper().Contains(term) || (l.Product.Description != null && l.Product.Description.ToUpper().Contains(term)) || l.Product.Barcodes.Any(b => b.Barcode.ToUpper().Contains(term)) || (l.SourceLocation != null && l.SourceLocation.Code.ToUpper().Contains(term)) || (l.DestinationLocation != null && l.DestinationLocation.Code.ToUpper().Contains(term)) || (l.Lot != null && l.Lot.NormalizedNumber.Contains(term)) || l.BalanceChanges.Any(c => (c.LotNumberSnapshot != null && c.LotNumberSnapshot.ToUpper().Contains(term)) || c.Location.Code.ToUpper().Contains(term)))); }
        return query;
    }
}
