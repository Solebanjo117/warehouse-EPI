using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>
/// Motor centralizado de consultas para movimientos efectivos de inventario.
/// Un movimiento es efectivo si no fue anulado por una corrección (OriginalMovementId)
/// ni es una transacción de reverso (ReversalMovementId).
/// </summary>
public static class EffectiveMovementQuery
{
    /// <summary>
    /// Filtra el conjunto de movimientos para conservar únicamente los movimientos efectivos vigentes.
    /// Excluye automáticamente originales corregidos y reversos, conservando movimientos normales y reemplazos activos.
    /// </summary>
    public static IQueryable<InventoryMovement> WhereEffective(
        this IQueryable<InventoryMovement> query,
        WarehouseDbContext dbContext)
    {
        return query.Where(m =>
            !dbContext.InventoryMovementCorrections.Any(c =>
                c.OriginalMovementId == m.Id || c.ReversalMovementId == m.Id));
    }

    /// <summary>
    /// Aplica los filtros analíticos de rango de fechas UTC, SKU, ubicación, tipo, propósito, usuario y búsqueda.
    /// </summary>
    public static IQueryable<InventoryMovement> ApplyFilter(
        this IQueryable<InventoryMovement> query,
        WarehouseDbContext dbContext,
        MovementReportFilter filter)
    {
        query = query.WhereEffective(dbContext);

        if (filter.FromUtc.HasValue)
            query = query.Where(m => m.OccurredAt >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(m => m.OccurredAt < filter.ToUtc.Value);

        if (filter.MovementType.HasValue)
            query = query.Where(m => m.Type == filter.MovementType.Value);

        if (filter.Purpose.HasValue)
            query = query.Where(m => m.Purpose == filter.Purpose.Value);

        if (filter.ResponsibleUserId.HasValue)
            query = query.Where(m => m.ResponsibleUserId == filter.ResponsibleUserId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Sku))
        {
            var productTerm = filter.Sku.Trim().ToUpperInvariant();
            query = query.Where(m => m.Lines.Any(l =>
                l.Product.Sku.ToUpper().Contains(productTerm) ||
                (l.Product.Description != null && l.Product.Description.ToUpper().Contains(productTerm)) ||
                (l.Product.ExternalReference != null && l.Product.ExternalReference.ToUpper().Contains(productTerm)) ||
                l.Product.Barcodes.Any(barcode => barcode.Barcode.ToUpper().Contains(productTerm))));
        }

        if (!string.IsNullOrWhiteSpace(filter.LocationCode))
        {
            var locationTerm = filter.LocationCode.Trim().ToUpperInvariant();
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

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var rawTerm = filter.Search.Trim();
            var term = rawTerm.ToUpperInvariant();
            var folioTerm = rawTerm.ToLowerInvariant();
            query = query.Where(m =>
                m.Id.ToString().Contains(folioTerm) ||
                m.OperationId.ToString().Contains(folioTerm) ||
                (m.Reference != null && m.Reference.ToUpper().Contains(term)) ||
                (m.Notes != null && m.Notes.ToUpper().Contains(term)) ||
                m.ResponsibleUser.FullName.ToUpper().Contains(term) ||
                m.Lines.Any(l =>
                    l.Product.Sku.ToUpper().Contains(term) ||
                    (l.Product.Description != null && l.Product.Description.ToUpper().Contains(term)) ||
                    (l.SourceLocation != null && l.SourceLocation.Code.ToUpper().Contains(term)) ||
                    (l.DestinationLocation != null && l.DestinationLocation.Code.ToUpper().Contains(term))));
        }

        return query;
    }

    /// <summary>
    /// Proyecta una entidad de movimiento cargada en memoria a su DTO inmutable de fila efectiva.
    /// </summary>
    public static EffectiveMovementRowDto ProjectToRowDto(InventoryMovement movement)
    {
        var lines = movement.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new EffectiveMovementLineDto(
                l.Id,
                movement.Id,
                l.ProductId,
                l.Product.Sku,
                l.Product.Description,
                l.UnitId,
                l.Unit.Code,
                l.SourceLocationId,
                l.SourceLocation?.Code,
                l.DestinationLocationId,
                l.DestinationLocation?.Code,
                l.Quantity,
                l.PreviousQuantity,
                l.AdjustmentDelta,
                l.LotAllocationMode.ToString(),
                l.BalanceChanges
                    .OrderBy(change => change.Location.Code)
                    .ThenBy(change => change.LotNumberSnapshot)
                    .Select(change => new EffectiveMovementBalanceChangeDto(
                        change.LocationId,
                        change.Location.Code,
                        change.LotId,
                        change.LotNumberSnapshot,
                        change.LotDateSnapshot,
                        change.PreviousQuantity,
                        change.DeltaQuantity,
                        change.ResultingQuantity))
                    .ToArray()))
            .ToArray();

        var distinctSkus = lines.Select(l => l.Sku).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        return new EffectiveMovementRowDto(
            movement.Id,
            movement.OperationId,
            movement.OccurredAt,
            movement.Type,
            movement.Purpose,
            movement.ResponsibleUser.FullName,
            movement.Reference,
            movement.Notes,
            movement.OperationalArea?.Code,
            lines.Length,
            distinctSkus,
            lines);
    }
}
