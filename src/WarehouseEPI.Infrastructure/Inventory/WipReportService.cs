using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record WipReportFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Search = null,
    Guid? WipAreaId = null,
    Guid? ResponsibleUserId = null);

public sealed record WipIssueRow(
    Guid MovementId,
    Guid MovementLineId,
    Guid ProductId,
    DateTimeOffset OccurredAt,
    string ProductSku,
    string? ProductDescription,
    string Unit,
    string SourceLocation,
    Guid WipAreaId,
    string WipArea,
    decimal Issued,
    decimal WarehouseReturned,
    decimal SupplierReturned,
    string Responsible,
    string? Reference,
    string? Notes)
{
    public decimal AssumedConsumed => Issued - WarehouseReturned - SupplierReturned;
    public decimal Returnable => AssumedConsumed;
}

public sealed record WipWeeklySummary(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    string ProductSku,
    string Unit,
    string WipArea,
    decimal Issued,
    decimal WarehouseReturned,
    decimal SupplierReturned)
{
    public decimal AssumedConsumed => Issued - WarehouseReturned - SupplierReturned;
}

public sealed record WipReportPage(
    IReadOnlyList<WipWeeklySummary> Summary,
    IReadOnlyList<WipIssueRow> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed class WipReportService(WarehouseDbContext dbContext, WarehouseClock warehouseClock)
{
    public async Task<IReadOnlyList<WipIssueRow>> SearchIssuesAsync(
        string? search,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var rows = await Query(new(null, null, search)).OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.MovementId).Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);
        var localized = new List<WipIssueRow>();
        foreach (var row in rows.Where(item => item.Returnable > 0))
            localized.Add(row with { OccurredAt = await warehouseClock.ConvertAsync(row.OccurredAt, cancellationToken) });
        return localized;
    }

    public async Task<WipIssueRow?> GetIssueAsync(Guid movementLineId, CancellationToken cancellationToken = default)
    {
        var row = await Query(new(null, null)).SingleOrDefaultAsync(
            item => item.MovementLineId == movementLineId, cancellationToken);
        return row is null ? null : row with
        {
            OccurredAt = await warehouseClock.ConvertAsync(row.OccurredAt, cancellationToken)
        };
    }

    public async Task<WipReportPage> GetPageAsync(
        WipReportFilter filter,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var all = await Query(filter).OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.MovementId).ToListAsync(cancellationToken);
        var localRows = new List<(WipIssueRow Row, DateOnly WeekStart)>();
        foreach (var row in all)
        {
            var local = await warehouseClock.ConvertAsync(row.OccurredAt, cancellationToken);
            var date = DateOnly.FromDateTime(local.DateTime);
            var daysFromMonday = ((int)local.DayOfWeek + 6) % 7;
            localRows.Add((row with { OccurredAt = local }, date.AddDays(-daysFromMonday)));
        }
        var summary = localRows.GroupBy(item => new
            {
                item.WeekStart,
                item.Row.ProductSku,
                item.Row.Unit,
                item.Row.WipArea
            })
            .OrderByDescending(group => group.Key.WeekStart)
            .ThenBy(group => group.Key.ProductSku, StringComparer.Ordinal)
            .ThenBy(group => group.Key.WipArea, StringComparer.Ordinal)
            .Select(group => new WipWeeklySummary(group.Key.WeekStart, group.Key.WeekStart.AddDays(6),
                group.Key.ProductSku, group.Key.Unit, group.Key.WipArea,
                group.Sum(item => item.Row.Issued), group.Sum(item => item.Row.WarehouseReturned),
                group.Sum(item => item.Row.SupplierReturned))).ToArray();
        var page = Math.Max(1, pageNumber);
        var size = Math.Clamp(pageSize, 1, 200);
        var localized = localRows.Select(item => item.Row).ToArray();
        return new(summary, localized.Skip((page - 1) * size).Take(size).ToArray(), localized.Length, page, size);
    }

    public async Task<IReadOnlyList<WipIssueRow>> ExportAsync(
        WipReportFilter filter,
        CancellationToken cancellationToken = default)
    {
        var rows = await Query(filter).OrderByDescending(item => item.OccurredAt).ThenBy(item => item.ProductSku)
            .ToListAsync(cancellationToken);
        var localized = new List<WipIssueRow>();
        foreach (var row in rows)
            localized.Add(row with { OccurredAt = await warehouseClock.ConvertAsync(row.OccurredAt, cancellationToken) });
        return localized;
    }

    private IQueryable<WipIssueRow> Query(WipReportFilter filter)
    {
        var query = dbContext.InventoryMovementLines.AsNoTracking()
            .Where(line => line.Movement.Type == InventoryMovementType.Exit &&
                line.Movement.Purpose == InventoryMovementPurpose.ProductionIssue &&
                line.Movement.OperationalAreaId != null &&
                !dbContext.InventoryMovementCorrections.Any(correction => correction.OriginalMovementId == line.MovementId));
        if (filter.From is not null) query = query.Where(line => line.Movement.OccurredAt >= filter.From);
        if (filter.To is not null) query = query.Where(line => line.Movement.OccurredAt < filter.To);
        if (filter.WipAreaId is not null) query = query.Where(line => line.Movement.OperationalAreaId == filter.WipAreaId);
        if (filter.ResponsibleUserId is not null) query = query.Where(line => line.Movement.ResponsibleUserId == filter.ResponsibleUserId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            query = query.Where(line => line.Product.Sku.ToUpper().Contains(term) ||
                (line.Product.Description != null && line.Product.Description.ToUpper().Contains(term)) ||
                (line.Movement.Reference != null && line.Movement.Reference.ToUpper().Contains(term)) ||
                line.Movement.ResponsibleUser.FullName.ToUpper().Contains(term) ||
                line.SourceLocation!.Code.ToUpper().Contains(term) ||
                line.Movement.OperationalArea!.Code.ToUpper().Contains(term) ||
                line.MovementId.ToString().Contains(term));
        }

        return query.Select(line => new WipIssueRow(
            line.MovementId,
            line.Id,
            line.ProductId,
            line.Movement.OccurredAt,
            line.Product.Sku,
            line.Product.Description,
            line.Unit.Code,
            line.SourceLocation!.Code,
            line.Movement.OperationalAreaId!.Value,
            line.Movement.OperationalArea!.Code,
            line.Quantity,
            dbContext.WipDispositions.Where(disposition =>
                    disposition.OriginalMovementLineId == line.Id &&
                    disposition.Type == WipDispositionType.WarehouseReturn &&
                    disposition.ReversesDispositionId == null &&
                    !dbContext.WipDispositions.Any(reversal => reversal.ReversesDispositionId == disposition.Id))
                .Sum(disposition => (decimal?)disposition.Quantity) ?? 0m,
            dbContext.WipDispositions.Where(disposition =>
                    disposition.OriginalMovementLineId == line.Id &&
                    disposition.Type == WipDispositionType.SupplierReturn &&
                    disposition.ReversesDispositionId == null &&
                    !dbContext.WipDispositions.Any(reversal => reversal.ReversesDispositionId == disposition.Id))
                .Sum(disposition => (decimal?)disposition.Quantity) ?? 0m,
            line.Movement.ResponsibleUser.FullName,
            line.Movement.Reference,
            line.Movement.Notes));
    }
}
