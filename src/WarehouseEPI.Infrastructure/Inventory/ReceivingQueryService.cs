using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed class ReceivingQueryService(WarehouseDbContext db)
{
    public async Task<ReceivingDocumentPage> SearchAsync(ReceivingDocumentFilter filter, CancellationToken token = default)
    {
        var query = db.ReceivingDocuments.AsNoTracking();
        if (filter.Status is not null) query = query.Where(item => item.Status == filter.Status);
        else query = query.Where(item => item.Status == ReceivingDocumentStatus.Open || item.Status == ReceivingDocumentStatus.PartiallyReceived);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            query = query.Where(item => item.NormalizedNumber.Contains(term) || item.NormalizedOrigin.Contains(term) ||
                item.Lines.Any(line => line.Product.Sku.Contains(term) || (line.Product.Description != null && line.Product.Description.ToUpper().Contains(term)) || line.Product.Barcodes.Any(code => code.IsActive && code.Barcode.ToUpper().Contains(term))));
        }
        var total = await query.CountAsync(token);
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var documents = await query.Include(item => item.Lines)
            .OrderBy(item => item.Status == ReceivingDocumentStatus.PartiallyReceived ? 0 : 1)
            .ThenBy(item => item.OpenedAt).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var totals = await EffectiveQuantitiesAsync(documents.Select(item => item.Id), token);
        return new(documents.Select(item =>
        {
            var expected = item.Lines.Sum(line => line.ExpectedQuantity);
            var received = totals.GetValueOrDefault(item.Id)?.Values.Sum() ?? 0m;
            return new ReceivingDocumentListRow(item.Id, item.Type, item.Number, item.Origin, item.Status, item.DocumentDate, item.OpenedAt, item.Lines.Count,
                expected == 0 ? 0 : Math.Round(Math.Min(received / expected * 100m, 999m), 1));
        }).ToArray(), total);
    }

    public async Task<ReceivingDocumentDetail?> GetAsync(Guid id, CancellationToken token = default)
    {
        var document = await db.ReceivingDocuments.AsNoTracking()
            .Include(item => item.OpenedByUser)
            .Include(item => item.Lines).ThenInclude(item => item.Product)
            .Include(item => item.Lines).ThenInclude(item => item.Unit)
            .Include(item => item.Confirmations).ThenInclude(item => item.ResponsibleUser)
            .Include(item => item.Confirmations).ThenInclude(item => item.Lines).ThenInclude(item => item.InventoryMovementLine).ThenInclude(item => item.Product)
            .Include(item => item.Confirmations).ThenInclude(item => item.Lines).ThenInclude(item => item.InventoryMovementLine).ThenInclude(item => item.Unit)
            .Include(item => item.Confirmations).ThenInclude(item => item.Lines).ThenInclude(item => item.InventoryMovementLine).ThenInclude(item => item.DestinationLocation)
            .Include(item => item.Events).ThenInclude(item => item.ActorUser)
            .SingleOrDefaultAsync(item => item.Id == id, token);
        if (document is null) return null;
        var totals = (await EffectiveQuantitiesAsync([id], token)).GetValueOrDefault(id) ?? [];
        return new(
            document.Id, document.Type, document.Number, document.Origin, document.DocumentDate, document.Status,
            document.Notes, document.Version, document.OpenedByUser.FullName, document.OpenedAt,
            document.CloseReason ?? document.CancelReason,
            document.Lines.OrderBy(item => item.LineNumber).Select(item => new ReceivingDocumentLineDetail(
                item.Id, item.ProductId, item.Product.Sku, item.Product.Description, item.Unit.Code,
                item.ExpectedQuantity, totals.GetValueOrDefault(item.ProductId), totals.GetValueOrDefault(item.ProductId) - item.ExpectedQuantity)).ToArray(),
            document.Confirmations.OrderByDescending(item => item.OccurredAt).Select(item => new ReceivingConfirmationDetail(
                item.Id, item.InventoryMovementId, item.OccurredAt, item.ResponsibleUser.FullName, item.DifferenceNotes,
                item.Lines.OrderBy(line => line.InventoryMovementLine.LineNumber).Select(line => new ReceivingConfirmationLineDetail(
                    line.InventoryMovementLine.Product.Sku, line.InventoryMovementLine.Quantity, line.InventoryMovementLine.Unit.Code,
                    line.InventoryMovementLine.DestinationLocation?.Code ?? "—", line.ExternalLotReference, line.ReceivingDocumentLineId is null)).ToArray())).ToArray(),
            document.Events.OrderByDescending(item => item.RecordedAt).ThenByDescending(item => item.Id).Select(item => new ReceivingDocumentEventDetail(
                item.Type, item.RecordedAt, item.ActorUser?.FullName ?? "Sistema", item.Notes)).ToArray());
    }

    public async Task<ReceivingMovementDocumentLink?> GetMovementLinkAsync(Guid movementId, CancellationToken token = default)
    {
        var originalId = await ResolveOriginalReceiptMovementIdAsync(movementId, token);
        var confirmation = await db.ReceivingConfirmations.AsNoTracking()
            .Include(item => item.ReceivingDocument)
            .Include(item => item.Lines)
            .SingleOrDefaultAsync(item => item.InventoryMovementId == originalId, token);
        return confirmation is null ? null : new(
            confirmation.ReceivingDocumentId,
            $"{FormatType(confirmation.ReceivingDocument.Type)} {confirmation.ReceivingDocument.Number}",
            confirmation.ReceivingDocument.Status,
            confirmation.DifferenceNotes,
            confirmation.Lines.Select(item => item.ExternalLotReference).Where(item => item is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray());
    }

    private async Task<Guid> ResolveOriginalReceiptMovementIdAsync(Guid movementId, CancellationToken token)
    {
        var correction = await db.InventoryMovementCorrections.AsNoTracking().SingleOrDefaultAsync(item =>
            item.OriginalMovementId == movementId || item.ReversalMovementId == movementId || item.ReplacementMovementId == movementId, token);
        return correction?.OriginalMovementId ?? movementId;
    }

    private async Task<Dictionary<Guid, Dictionary<Guid, decimal>>> EffectiveQuantitiesAsync(IEnumerable<Guid> documentIds, CancellationToken token)
    {
        var ids = documentIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var confirmations = await db.ReceivingConfirmations.AsNoTracking().Where(item => ids.Contains(item.ReceivingDocumentId))
            .Select(item => new { item.ReceivingDocumentId, item.InventoryMovementId }).ToListAsync(token);
        var originalIds = confirmations.Select(item => item.InventoryMovementId).ToArray();
        var corrections = await db.InventoryMovementCorrections.AsNoTracking().Where(item => originalIds.Contains(item.OriginalMovementId)).ToListAsync(token);
        var correctionByOriginal = corrections.ToDictionary(item => item.OriginalMovementId);
        var effective = confirmations.Select(item => new
        {
            item.ReceivingDocumentId,
            MovementId = correctionByOriginal.TryGetValue(item.InventoryMovementId, out var correction) ? correction.ReplacementMovementId : item.InventoryMovementId
        }).Where(item => item.MovementId is not null).ToArray();
        var movementIds = effective.Select(item => item.MovementId!.Value).ToArray();
        var lines = await db.InventoryMovementLines.AsNoTracking().Where(item => movementIds.Contains(item.MovementId))
            .Select(item => new { item.MovementId, item.ProductId, item.Quantity }).ToListAsync(token);
        var documentByMovement = effective.ToDictionary(item => item.MovementId!.Value, item => item.ReceivingDocumentId);
        return lines.GroupBy(item => documentByMovement[item.MovementId]).ToDictionary(
            group => group.Key,
            group => group.GroupBy(item => item.ProductId).ToDictionary(items => items.Key, items => items.Sum(item => item.Quantity)));
    }

    public static string FormatType(ReceivingDocumentType type) => type switch
    {
        ReceivingDocumentType.PurchaseOrder => "OC",
        ReceivingDocumentType.DeliveryNote => "Remisión",
        ReceivingDocumentType.PackingList => "Packing list",
        ReceivingDocumentType.ProductionOrder => "Orden de producción",
        _ => "Otro"
    };
}
