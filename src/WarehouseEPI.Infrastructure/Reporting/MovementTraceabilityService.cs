using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed record MovementTraceabilityResource(
    string Id,
    string Kind,
    string Label,
    string Status,
    string Summary,
    string DetailsUrl);

public sealed record MovementTraceabilityEvent(
    string Id,
    DateTimeOffset OccurredAt,
    string Category,
    string Title,
    string Summary,
    string Status,
    string Responsible,
    string? DetailsUrl,
    bool ChangesInventory,
    bool IsCurrentMovement);

public sealed record MovementTraceabilityContext(
    string CurrentMovementStatus,
    IReadOnlyList<MovementTraceabilityResource> Resources,
    IReadOnlyList<MovementTraceabilityEvent> Events)
{
    public bool HasAdditionalContext => Resources.Count > 0 || Events.Count > 1;
}

/// <summary>
/// Builds the read-only causal history shown from a single inventory movement.
/// Related records are anchored to the complete correction chain so opening the
/// original, reversal, or replacement returns the same audit context.
/// </summary>
public sealed class MovementTraceabilityService(WarehouseDbContext db)
{
    public async Task<MovementTraceabilityContext?> GetAsync(Guid movementId, CancellationToken token = default)
    {
        var correction = await db.InventoryMovementCorrections.AsNoTracking()
            .Include(item => item.RequestedByUser)
            .Include(item => item.AuthorizedByUser)
            .SingleOrDefaultAsync(item => item.OriginalMovementId == movementId ||
                item.ReversalMovementId == movementId || item.ReplacementMovementId == movementId, token);

        var chainIds = correction is null
            ? [movementId]
            : new[] { correction.OriginalMovementId, correction.ReversalMovementId }
                .Concat(correction.ReplacementMovementId is Guid replacementId ? [replacementId] : [])
                .Distinct()
                .ToArray();

        var movements = await db.InventoryMovements.AsNoTracking()
            .Where(item => chainIds.Contains(item.Id))
            .Include(item => item.ResponsibleUser)
            .Include(item => item.OperationalArea)
            .Include(item => item.Lines).ThenInclude(line => line.Product).ThenInclude(product => product.BaseUnit)
            .OrderBy(item => item.OccurredAt).ThenBy(item => item.Id)
            .ToListAsync(token);
        if (movements.All(item => item.Id != movementId)) return null;

        var resources = new List<MovementTraceabilityResource>();
        var events = new List<MovementTraceabilityEvent>();
        foreach (var movement in movements)
        {
            var status = MovementStatus(movement.Id, correction);
            var lines = movement.Lines.OrderBy(line => line.LineNumber)
                .Select(line => $"{line.Product.Sku} · {line.Quantity:0.####} {line.Product.BaseUnit.Code}")
                .ToArray();
            var summary = lines.Length == 0
                ? PurposeLabel(movement.Purpose)
                : string.Join("; ", lines.Take(3)) + (lines.Length > 3 ? $"; +{lines.Length - 3} línea(s)" : string.Empty);
            events.Add(new(
                $"movement:{movement.Id:N}", movement.OccurredAt, "Movimiento", TypeLabel(movement.Type), summary,
                status, movement.ResponsibleUser.FullName,
                $"/Admin/Inventory/Movements/Details/{movement.Id}", true, movement.Id == movementId));
        }

        if (correction is not null)
        {
            events.Add(new(
                $"correction:{correction.Id:N}", correction.RecordedAt, "Corrección", "Corrección autorizada",
                correction.Reason,
                correction.ReplacementMovementId is null ? "Reverso sin reemplazo" : "Reverso y reemplazo",
                correction.AuthorizedByUser.FullName,
                $"/Admin/Inventory/Movements/Details/{correction.OriginalMovementId}", false, false));
        }

        await AddReceivingAsync(chainIds, resources, events, token);
        await AddWipAsync(chainIds, movements, resources, events, token);
        await AddCycleCountAsync(chainIds, resources, events, token);

        var orderedResources = resources
            .DistinctBy(item => (item.Kind, item.DetailsUrl))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ToArray();
        var orderedEvents = events
            .DistinctBy(item => item.Id)
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var currentStatus = orderedEvents.Single(item => item.IsCurrentMovement).Status;
        return new(currentStatus, orderedResources, orderedEvents);
    }

    private async Task AddReceivingAsync(
        Guid[] chainIds,
        List<MovementTraceabilityResource> resources,
        List<MovementTraceabilityEvent> events,
        CancellationToken token)
    {
        var confirmation = await db.ReceivingConfirmations.AsNoTracking()
            .Where(item => chainIds.Contains(item.InventoryMovementId))
            .Include(item => item.ReceivingDocument)
            .Include(item => item.ResponsibleUser)
            .Include(item => item.Lines)
            .OrderBy(item => item.OccurredAt)
            .FirstOrDefaultAsync(token);
        if (confirmation is null) return;

        var document = confirmation.ReceivingDocument;
        var documentLabel = $"{ReceivingTypeLabel(document.Type)} {document.Number}";
        var documentUrl = $"/Operations/Receiving/{document.Id}";
        var externalLots = confirmation.Lines.Select(item => item.ExternalLotReference)
            .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var summaryParts = new List<string> { $"Origen: {document.Origin}" };
        if (confirmation.DifferenceAcknowledged) summaryParts.Add("Diferencia confirmada");
        if (!string.IsNullOrWhiteSpace(confirmation.DifferenceNotes)) summaryParts.Add(confirmation.DifferenceNotes);
        if (externalLots.Length > 0) summaryParts.Add($"Lote/rollo externo: {string.Join(", ", externalLots)}");
        resources.Add(new(
            $"receiving:{document.Id:N}", "Recepción", documentLabel, ReceivingStatusLabel(document.Status),
            string.Join(" · ", summaryParts), documentUrl));
        var directEvent = await db.ReceivingDocumentEvents.AsNoTracking()
            .Where(item => item.ReceivingDocumentId == document.Id && item.OperationId == confirmation.OperationId)
            .Include(item => item.ActorUser)
            .SingleOrDefaultAsync(token);
        events.Add(directEvent is null
            ? new(
                $"receiving-confirmation:{confirmation.Id:N}", confirmation.OccurredAt, "Recepción", "Recepción confirmada",
                string.Join(" · ", summaryParts.Skip(1).DefaultIfEmpty(documentLabel)),
                ReceivingStatusLabel(document.Status), confirmation.ResponsibleUser.FullName, documentUrl, false, false)
            : new(
                $"receiving-event:{directEvent.Id:N}", directEvent.RecordedAt, "Recepción", ReceivingEventLabel(directEvent.Type),
                directEvent.Notes ?? string.Join(" · ", summaryParts.Skip(1).DefaultIfEmpty(documentLabel)),
                ReceivingStatusLabel(document.Status), directEvent.ActorUser?.FullName ?? "Sistema", documentUrl, false, false));
    }

    private async Task AddWipAsync(
        Guid[] chainIds,
        IReadOnlyList<InventoryMovement> movements,
        List<MovementTraceabilityResource> resources,
        List<MovementTraceabilityEvent> events,
        CancellationToken token)
    {
        var dispositions = await db.WipDispositions.AsNoTracking()
            .Where(item => (item.InventoryMovementId != null && chainIds.Contains(item.InventoryMovementId.Value)) ||
                chainIds.Contains(item.OriginalMovementLine.MovementId))
            .Include(item => item.ResponsibleUser)
            .Include(item => item.DestinationLocation)
            .Include(item => item.OriginalMovementLine).ThenInclude(line => line.Product).ThenInclude(product => product.BaseUnit)
            .Include(item => item.OriginalMovementLine).ThenInclude(line => line.Movement).ThenInclude(movement => movement.OperationalArea)
            .OrderBy(item => item.OccurredAt).ThenBy(item => item.Id)
            .ToListAsync(token);

        var wipMovements = movements.Where(item => IsWip(item.Purpose)).ToArray();
        foreach (var movement in wipMovements)
        {
            resources.Add(new(
                $"wip-movement:{movement.Id:N}", "WIP", PurposeLabel(movement.Purpose),
                "Movimiento de inventario", movement.OperationalArea?.Code ?? "Flujo de inventario WIP",
                "/Reports/Wip"));
        }

        var reversedIds = dispositions.Where(item => item.ReversesDispositionId is not null)
            .Select(item => item.ReversesDispositionId!.Value).ToHashSet();
        foreach (var disposition in dispositions)
        {
            var line = disposition.OriginalMovementLine;
            var issueUrl = $"/Reports/Wip/Details/{line.Id}";
            var type = disposition.Type == WipDispositionType.WarehouseReturn
                ? "Regreso WIP a bodega"
                : "Devolución WIP a proveedor";
            var status = disposition.ReversesDispositionId is not null
                ? "Reverso"
                : reversedIds.Contains(disposition.Id) ? "Corregido" : "Vigente";
            var route = disposition.DestinationLocation?.Code ?? line.Movement.OperationalArea?.Code ?? "WIP";
            var summary = $"{line.Product.Sku} · {disposition.Quantity:0.####} {line.Product.BaseUnit.Code} · {route}";
            if (!string.IsNullOrWhiteSpace(disposition.Reference)) summary += $" · Ref. {disposition.Reference}";
            resources.Add(new(
                $"wip:{line.Id:N}", "WIP", $"Flujo WIP · {line.Product.Sku}", "Historial relacionado",
                line.Movement.OperationalArea?.Code ?? "Área WIP", issueUrl));
            events.Add(new(
                $"wip-disposition:{disposition.Id:N}", disposition.OccurredAt, "WIP", type, summary, status,
                disposition.ResponsibleUser.FullName, issueUrl, false, false));
        }
    }

    private async Task AddCycleCountAsync(
        Guid[] chainIds,
        List<MovementTraceabilityResource> resources,
        List<MovementTraceabilityEvent> events,
        CancellationToken token)
    {
        var location = await db.CycleCountLocations.AsNoTracking()
            .Where(item => item.AdjustmentMovementId != null && chainIds.Contains(item.AdjustmentMovementId.Value))
            .Include(item => item.Campaign)
            .Include(item => item.Location)
            .FirstOrDefaultAsync(token);
        if (location is null) return;

        var url = $"/Operations/CycleCounts/Details/{location.CampaignId}";
        resources.Add(new(
            $"cycle-count:{location.Id:N}", "Conteo", $"Campaña {location.Campaign.Number}",
            CycleLocationStatusLabel(location.Status), $"Ubicación {location.Location.Code}", url));

        var actions = await db.CycleCountActions.AsNoTracking()
            .Where(item => item.CycleCountLocationId == location.Id)
            .Include(item => item.ResponsibleUser)
            .OrderBy(item => item.RecordedAt).ThenBy(item => item.Id)
            .ToListAsync(token);
        foreach (var action in actions)
        {
            events.Add(new(
                $"cycle-count-action:{action.Id:N}", action.RecordedAt, "Conteo", CycleActionLabel(action.Type),
                action.Notes ?? $"Campaña {location.Campaign.Number} · {location.Location.Code}",
                CycleLocationStatusLabel(location.Status), action.ResponsibleUser.FullName, url, false, false));
        }
    }

    private static string MovementStatus(Guid movementId, InventoryMovementCorrection? correction)
    {
        if (correction is null) return "Vigente";
        if (correction.OriginalMovementId == movementId) return "Original corregido";
        if (correction.ReversalMovementId == movementId) return "Reverso";
        return "Reemplazo";
    }

    private static string TypeLabel(InventoryMovementType type) => type switch
    {
        InventoryMovementType.Entry => "Entrada",
        InventoryMovementType.Exit => "Salida",
        InventoryMovementType.Transfer => "Transferencia",
        InventoryMovementType.Adjustment => "Ajuste",
        _ => "Movimiento"
    };

    private static string PurposeLabel(InventoryMovementPurpose purpose) => purpose switch
    {
        InventoryMovementPurpose.ProductionIssue => "Surtimiento WIP",
        InventoryMovementPurpose.WipWarehouseReturn => "Devolución WIP a bodega",
        InventoryMovementPurpose.WipConsumption => "Consumo WIP",
        InventoryMovementPurpose.WipSupplierReturn => "Devolución WIP a proveedor",
        InventoryMovementPurpose.GeneralExit => "Salida general",
        InventoryMovementPurpose.CycleCountAdjustment => "Ajuste por conteo cíclico",
        InventoryMovementPurpose.DocumentReceipt => "Recepción por documento",
        _ => "Movimiento estándar"
    };

    private static bool IsWip(InventoryMovementPurpose purpose) => purpose is
        InventoryMovementPurpose.ProductionIssue or InventoryMovementPurpose.WipWarehouseReturn or
        InventoryMovementPurpose.WipConsumption or InventoryMovementPurpose.WipSupplierReturn;

    private static string ReceivingTypeLabel(ReceivingDocumentType type) => type switch
    {
        ReceivingDocumentType.PurchaseOrder => "Orden de compra",
        ReceivingDocumentType.DeliveryNote => "Nota de entrega",
        ReceivingDocumentType.PackingList => "Lista de empaque",
        ReceivingDocumentType.ProductionOrder => "Orden de producción",
        _ => "Documento"
    };

    private static string ReceivingStatusLabel(ReceivingDocumentStatus status) => status switch
    {
        ReceivingDocumentStatus.Open => "Abierto",
        ReceivingDocumentStatus.PartiallyReceived => "Parcial",
        ReceivingDocumentStatus.Completed => "Completado",
        ReceivingDocumentStatus.ClosedWithDifferences => "Cerrado con diferencias",
        _ => "Cancelado"
    };

    private static string ReceivingEventLabel(ReceivingDocumentEventType type) => type switch
    {
        ReceivingDocumentEventType.Opened => "Documento abierto",
        ReceivingDocumentEventType.ReceiptConfirmed => "Recepción confirmada",
        ReceivingDocumentEventType.AutomaticallyCompleted => "Documento completado automáticamente",
        ReceivingDocumentEventType.ClosedWithDifferences => "Documento cerrado con diferencias",
        ReceivingDocumentEventType.Cancelled => "Documento cancelado",
        ReceivingDocumentEventType.ReceiptCorrected => "Recepción corregida",
        _ => "Documento reabierto tras corrección"
    };

    private static string CycleLocationStatusLabel(CycleCountLocationStatus status) => status switch
    {
        CycleCountLocationStatus.Pending => "Pendiente",
        CycleCountLocationStatus.Counting => "En conteo",
        CycleCountLocationStatus.UnderReview => "En revisión",
        CycleCountLocationStatus.RecountRequested => "Reconteo solicitado",
        CycleCountLocationStatus.Stale => "Desactualizado",
        CycleCountLocationStatus.Completed => "Completado",
        _ => "Cancelado"
    };

    private static string CycleActionLabel(CycleCountActionType type) => type switch
    {
        CycleCountActionType.Created => "Campaña creada",
        CycleCountActionType.Released => "Campaña liberada",
        CycleCountActionType.AttemptStarted => "Conteo iniciado",
        CycleCountActionType.AttemptSubmitted => "Conteo enviado",
        CycleCountActionType.RecountRequested => "Reconteo solicitado",
        CycleCountActionType.StaleDetected => "Diferencia desactualizada",
        CycleCountActionType.AdjustmentApproved => "Ajuste aprobado",
        CycleCountActionType.BatchReviewed => "Lote de revisión procesado",
        CycleCountActionType.LocationCompleted => "Ubicación conciliada",
        CycleCountActionType.CampaignCompleted => "Campaña completada",
        _ => "Conteo cancelado"
    };
}
