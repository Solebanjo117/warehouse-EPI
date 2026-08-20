using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>
/// Servicio de consulta paginada y filtrada para movimientos efectivos de inventario.
/// </summary>
public sealed class MovementReportService(WarehouseDbContext dbContext)
{
    /// <summary>
    /// Obtiene una página de movimientos efectivos aplicando filtros de fecha, tipo, propósito, SKU, ubicación y búsqueda.
    /// </summary>
    public async Task<EffectiveMovementPage> GetMovementsPageAsync(
        MovementReportFilter filter,
        CancellationToken cancellationToken = default)
    {
        var pageSize = filter.PageSize <= 0 ? 25 : Math.Min(filter.PageSize, 100);
        var pageNumber = Math.Max(1, filter.PageNumber);

        var query = dbContext.InventoryMovements
            .AsNoTracking()
            .ApplyFilter(dbContext, filter);

        var totalCount = await query.CountAsync(cancellationToken);

        var movements = await query
            .Include(m => m.ResponsibleUser)
            .Include(m => m.OperationalArea)
            .Include(m => m.Lines)
                .ThenInclude(l => l.Product)
            .Include(m => m.Lines)
                .ThenInclude(l => l.Unit)
            .Include(m => m.Lines)
                .ThenInclude(l => l.SourceLocation)
            .Include(m => m.Lines)
                .ThenInclude(l => l.DestinationLocation)
            .Include(m => m.Lines)
                .ThenInclude(l => l.BalanceChanges)
                    .ThenInclude(change => change.Location)
            .OrderByDescending(m => m.OccurredAt)
            .ThenByDescending(m => m.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var rowDtos = movements.Select(EffectiveMovementQuery.ProjectToRowDto).ToArray();

        return new EffectiveMovementPage(rowDtos, totalCount, pageNumber, pageSize);
    }

    /// <summary>
    /// Obtiene la lista de movimientos efectivos para exportación masiva con límite configurable.
    /// </summary>
    public async Task<EffectiveMovementExportBatch> GetMovementsForExportAsync(
        MovementReportFilter filter,
        int maxRows = 10000,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(maxRows, 1, 50000);

        var query = dbContext.InventoryMovements
            .AsNoTracking()
            .ApplyFilter(dbContext, filter);

        var totalOperations = await query.CountAsync(cancellationToken);
        var totalRows = await query.SelectMany(movement => movement.Lines).CountAsync(cancellationToken);
        if (totalRows > limit)
            return new([], totalOperations, totalRows, limit);

        var movements = await query
            .Include(m => m.ResponsibleUser)
            .Include(m => m.OperationalArea)
            .Include(m => m.Lines)
                .ThenInclude(l => l.Product)
            .Include(m => m.Lines)
                .ThenInclude(l => l.Unit)
            .Include(m => m.Lines)
                .ThenInclude(l => l.SourceLocation)
            .Include(m => m.Lines)
                .ThenInclude(l => l.DestinationLocation)
            .Include(m => m.Lines)
                .ThenInclude(l => l.BalanceChanges)
                    .ThenInclude(change => change.Location)
            .OrderByDescending(m => m.OccurredAt)
            .ThenByDescending(m => m.Id)
            .ToListAsync(cancellationToken);

        return new(
            movements.Select(EffectiveMovementQuery.ProjectToRowDto).ToArray(),
            totalOperations,
            totalRows,
            limit);
    }
}
