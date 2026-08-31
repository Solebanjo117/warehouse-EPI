using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed record UnifiedTraceFilter(DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? Search, string? Kind, int Page = 1, int PageSize = 25);
public sealed record UnifiedTraceRow(string Id, DateTimeOffset OccurredAt, string Kind, string Title, string Summary, string Status, bool? IsEffective, string Responsible, string? ProductSku, decimal? Quantity, string? Unit, string? Location, string? InternalLot, string? ExternalLot, Guid? DocumentId, Guid? MovementId, string DetailsUrl);
public sealed record UnifiedTracePage(IReadOnlyList<UnifiedTraceRow> Items, int TotalCount);
public sealed record UnifiedTraceExportBatch(IReadOnlyList<UnifiedTraceRow> Items, int TotalRows, int MaximumRows) { public bool ExceedsLimit => TotalRows > MaximumRows; }

public sealed class UnifiedTraceService(WarehouseDbContext db)
{
    public async Task<UnifiedTracePage> SearchAsync(UnifiedTraceFilter filter, CancellationToken token = default)
    {
        var sources = BuildSources(filter);
        var total = 0;
        var take = checked(Math.Max(1, filter.Page) * Math.Clamp(filter.PageSize, 1, 100));
        var candidates = new List<UnifiedTraceRow>();
        foreach (var source in sources)
        {
            total += await source.CountAsync(token);
            candidates.AddRange(await source.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id).Take(take).ToListAsync(token));
        }
        var items = candidates.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .Skip((Math.Max(1, filter.Page) - 1) * Math.Clamp(filter.PageSize, 1, 100)).Take(Math.Clamp(filter.PageSize, 1, 100)).ToArray();
        return new(items, total);
    }

    public async Task<UnifiedTraceExportBatch> ExportAsync(UnifiedTraceFilter filter, int maximumRows = 10000, CancellationToken token = default)
    {
        var limit = Math.Clamp(maximumRows, 1, 50000);
        var sources = BuildSources(filter);
        var total = 0;
        foreach (var source in sources) total += await source.CountAsync(token);
        if (total > limit) return new([], total, limit);
        var rows = new List<UnifiedTraceRow>(total);
        foreach (var source in sources) rows.AddRange(await source.ToListAsync(token));
        return new(rows.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id, StringComparer.Ordinal).ToArray(), total, limit);
    }

    private IReadOnlyList<IQueryable<UnifiedTraceRow>> BuildSources(UnifiedTraceFilter filter)
    {
        var kind = filter.Kind?.Trim().ToLowerInvariant();
        var sources = new List<IQueryable<UnifiedTraceRow>>();
        if (kind is null or "" or "movement") sources.Add(MovementRows(filter));
        if (kind is null or "" or "document") sources.Add(DocumentRows(filter));
        if (kind is null or "" or "wip") sources.Add(WipRows(filter));
        if (kind is null or "" or "count") sources.Add(CountRows(filter));
        return sources;
    }

    private IQueryable<UnifiedTraceRow> MovementRows(UnifiedTraceFilter filter)
    {
        var query = db.InventoryMovementLines.AsNoTracking();
        if (filter.FromUtc is not null) query = query.Where(item => item.Movement.OccurredAt >= filter.FromUtc);
        if (filter.ToUtc is not null) query = query.Where(item => item.Movement.OccurredAt < filter.ToUtc);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var raw = filter.Search.Trim(); var term = raw.ToUpperInvariant(); var guid = raw.ToLowerInvariant();
            query = query.Where(item => item.MovementId.ToString().Contains(guid) || item.Movement.OperationId.ToString().Contains(guid) ||
                item.Product.Sku.ToUpper().Contains(term) || (item.Product.Description != null && item.Product.Description.ToUpper().Contains(term)) ||
                item.Product.Barcodes.Any(code => code.Barcode.ToUpper().Contains(term)) ||
                (item.SourceLocation != null && item.SourceLocation.Code.ToUpper().Contains(term)) || (item.DestinationLocation != null && item.DestinationLocation.Code.ToUpper().Contains(term)) ||
                (item.Lot != null && item.Lot.NormalizedNumber.Contains(term)) || (item.Movement.Reference != null && item.Movement.Reference.ToUpper().Contains(term)) ||
                db.ReceivingConfirmationLines.Any(link => link.InventoryMovementLineId == item.Id && link.ExternalLotReference != null && link.ExternalLotReference.ToUpper().Contains(term)) ||
                db.ReceivingConfirmations.Any(receipt => receipt.InventoryMovementId == item.MovementId && (receipt.ReceivingDocument.NormalizedNumber.Contains(term) || receipt.ReceivingDocument.NormalizedOrigin.Contains(term))));
        }
        return query.Select(item => new UnifiedTraceRow(
            "M:" + item.Id, item.Movement.OccurredAt, "Movimiento",
            item.Movement.Type == InventoryMovementType.Entry ? "Entrada" : item.Movement.Type == InventoryMovementType.Exit ? "Salida" : item.Movement.Type == InventoryMovementType.Transfer ? "Transferencia" : "Ajuste",
            item.Product.Sku + " · " + item.Quantity + " " + item.Unit.Code,
            db.InventoryMovementCorrections.Any(correction => correction.OriginalMovementId == item.MovementId) ? "Original corregido" : db.InventoryMovementCorrections.Any(correction => correction.ReversalMovementId == item.MovementId) ? "Reverso" : "Vigente",
            !db.InventoryMovementCorrections.Any(correction => correction.OriginalMovementId == item.MovementId || correction.ReversalMovementId == item.MovementId),
            item.Movement.ResponsibleUser.FullName, item.Product.Sku, item.Quantity, item.Unit.Code,
            item.DestinationLocation != null ? item.DestinationLocation.Code : item.SourceLocation != null ? item.SourceLocation.Code : item.Movement.OperationalArea != null ? item.Movement.OperationalArea.Code : null,
            item.Lot != null ? item.Lot.Number : null,
            db.ReceivingConfirmationLines.Where(link => link.InventoryMovementLineId == item.Id).Select(link => link.ExternalLotReference).FirstOrDefault(),
            db.ReceivingConfirmations.Where(receipt => receipt.InventoryMovementId == item.MovementId).Select(receipt => (Guid?)receipt.ReceivingDocumentId).FirstOrDefault(),
            item.MovementId, "/Admin/Inventory/Movements/Details/" + item.MovementId));
    }

    private IQueryable<UnifiedTraceRow> DocumentRows(UnifiedTraceFilter filter)
    {
        var query = db.ReceivingDocumentEvents.AsNoTracking();
        if (filter.FromUtc is not null) query = query.Where(item => item.RecordedAt >= filter.FromUtc);
        if (filter.ToUtc is not null) query = query.Where(item => item.RecordedAt < filter.ToUtc);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var raw = filter.Search.Trim(); var term = raw.ToUpperInvariant(); var guid = raw.ToLowerInvariant();
            query = query.Where(item => item.ReceivingDocumentId.ToString().Contains(guid) || item.ReceivingDocument.OperationId.ToString().Contains(guid) ||
                item.ReceivingDocument.NormalizedNumber.Contains(term) || item.ReceivingDocument.NormalizedOrigin.Contains(term) ||
                item.ReceivingDocument.Lines.Any(line => line.Product.Sku.ToUpper().Contains(term) || line.Product.Barcodes.Any(code => code.Barcode.ToUpper().Contains(term))) ||
                (item.Notes != null && item.Notes.ToUpper().Contains(term)));
        }
        return query.Select(item => new UnifiedTraceRow(
            "D:" + item.Id, item.RecordedAt, "Documento", item.Type.ToString(),
            item.ReceivingDocument.Number + " · " + item.ReceivingDocument.Origin,
            item.ReceivingDocument.Status.ToString(), null, item.ActorUser != null ? item.ActorUser.FullName : "Sistema",
            null, null, null, null, null, null, item.ReceivingDocumentId, null,
            "/Operations/Receiving/" + item.ReceivingDocumentId));
    }

    private IQueryable<UnifiedTraceRow> WipRows(UnifiedTraceFilter filter)
    {
        var query = db.WipDispositions.AsNoTracking();
        if (filter.FromUtc is not null) query = query.Where(item => item.OccurredAt >= filter.FromUtc);
        if (filter.ToUtc is not null) query = query.Where(item => item.OccurredAt < filter.ToUtc);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var raw = filter.Search.Trim(); var term = raw.ToUpperInvariant(); var guid = raw.ToLowerInvariant();
            query = query.Where(item => item.Id.ToString().Contains(guid) || item.OperationId.ToString().Contains(guid) || item.OriginalMovementLine.Product.Sku.ToUpper().Contains(term) ||
                item.OriginalMovementLine.Movement.OperationalArea!.Code.ToUpper().Contains(term) || (item.Reference != null && item.Reference.ToUpper().Contains(term)));
        }
        return query.Select(item => new UnifiedTraceRow(
            "W:" + item.Id, item.OccurredAt, "WIP", item.Type == WipDispositionType.WarehouseReturn ? "Regreso WIP" : "Devolución WIP a proveedor",
            item.OriginalMovementLine.Product.Sku + " · " + item.Quantity + " " + item.OriginalMovementLine.Unit.Code,
            item.ReversesDispositionId == null && !db.WipDispositions.Any(reversal => reversal.ReversesDispositionId == item.Id) ? "Vigente" : item.ReversesDispositionId != null ? "Reverso" : "Corregido",
            item.ReversesDispositionId == null && !db.WipDispositions.Any(reversal => reversal.ReversesDispositionId == item.Id), item.ResponsibleUser.FullName,
            item.OriginalMovementLine.Product.Sku, item.Quantity, item.OriginalMovementLine.Unit.Code,
            item.DestinationLocation != null ? item.DestinationLocation.Code : item.OriginalMovementLine.Movement.OperationalArea!.Code,
            item.OriginalMovementLine.Lot != null ? item.OriginalMovementLine.Lot.Number : null, null, null, item.InventoryMovementId,
            item.InventoryMovementId != null ? "/Admin/Inventory/Movements/Details/" + item.InventoryMovementId : "/Reports/Wip"));
    }

    private IQueryable<UnifiedTraceRow> CountRows(UnifiedTraceFilter filter)
    {
        var query = db.CycleCountActions.AsNoTracking();
        if (filter.FromUtc is not null) query = query.Where(item => item.RecordedAt >= filter.FromUtc);
        if (filter.ToUtc is not null) query = query.Where(item => item.RecordedAt < filter.ToUtc);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var raw = filter.Search.Trim(); var term = raw.ToUpperInvariant(); var guid = raw.ToLowerInvariant();
            query = query.Where(item => item.Id.ToString().Contains(guid) || (item.OperationId != null && item.OperationId.ToString()!.Contains(guid)) ||
                item.Campaign.Number.ToString().Contains(raw) || (item.Campaign.Title != null && item.Campaign.Title.ToUpper().Contains(term)) || (item.CycleCountLocation != null && item.CycleCountLocation.Location.Code.ToUpper().Contains(term)) ||
                (item.Notes != null && item.Notes.ToUpper().Contains(term)));
        }
        return query.Select(item => new UnifiedTraceRow(
            "C:" + item.Id, item.RecordedAt, "Conteo", item.Type.ToString(), "Campaña " + item.Campaign.Number,
            item.Campaign.Status.ToString(), null, item.ResponsibleUser.FullName, null, null, null,
            item.CycleCountLocation != null ? item.CycleCountLocation.Location.Code : null, null, null, null,
            item.CycleCountLocation != null ? item.CycleCountLocation.AdjustmentMovementId : null,
            item.CycleCountLocation != null ? "/Operations/CycleCounts/Details/" + item.CampaignId : "/Operations/CycleCounts"));
    }
}
