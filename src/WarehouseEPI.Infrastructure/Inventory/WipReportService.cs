using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record WipReportFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Search = null,
    Guid? WipAreaId = null,
    Guid? ResponsibleUserId = null,
    DateTimeOffset? AgedBefore = null,
    bool RequireNoEffectiveReturn = false);

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

public sealed record WipInventoryRow(
    Guid WipAreaId,
    string WipArea,
    Guid ProductId,
    string ProductSku,
    string? ProductDescription,
    string Unit,
    decimal Quantity,
    DateOnly? OldestPositiveLotDate,
    DateTimeOffset UpdatedAt,
    Guid DocumentId = default, bool IsOpening = false, decimal Delivered = 0, decimal Used = 0,
    decimal Scrapped = 0, decimal WarehouseReturned = 0, decimal SupplierReturned = 0,
    string Responsible = "", Guid? MovementLineId = null);

public sealed record WipActivityRow(
    Guid MovementId,
    DateTimeOffset OccurredAt,
    Guid WipAreaId,
    string WipArea,
    Guid ProductId,
    string ProductSku,
    string? ProductDescription,
    string Unit,
    string Category,
    string? SourceLocation,
    string? DestinationLocation,
    decimal Delta,
    string Responsible,
    string? Reference,
    string? Notes);

public sealed record WipTrackedReportPage(
    IReadOnlyList<WipInventoryRow> Inventory,
    IReadOnlyList<WipActivityRow> Activity,
    int TotalActivityCount,
    int PageNumber,
    int PageSize);

public sealed class WipReportService(WarehouseDbContext dbContext, WarehouseClock warehouseClock)
{
    public async Task<WipTrackedReportPage> GetTrackedPageAsync(
        WipReportFilter filter,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.WipDocuments.AsNoTracking().Where(x => !x.IsCancelled);
        if (filter.WipAreaId is Guid area) query = query.Where(x => x.WipLocationId == area);
        if (filter.ResponsibleUserId is Guid responsible) query = query.Where(x => x.ResponsibleUserId == responsible);
        if (filter.From is DateTimeOffset from) query = query.Where(x => x.OccurredAt >= from);
        if (filter.To is DateTimeOffset to) query = query.Where(x => x.OccurredAt < to);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            query = query.Where(x => x.Product.Sku.ToUpper().Contains(term) ||
                (x.Product.Description != null && x.Product.Description.ToUpper().Contains(term)) || x.WipLocation.Code.ToUpper().Contains(term));
        }
        if (filter.AgedBefore is DateTimeOffset aged)
            query = query.Where(x => x.OccurredAt <= aged && x.Quantity > x.Applications
                .Where(a => a.Kind != WipDocumentApplicationKind.Reversal && !dbContext.WipDocumentApplications.Any(r => r.ReversesApplicationId == a.Id)).Sum(a => a.Quantity));
        var total = await query.CountAsync(cancellationToken);
        var size = Math.Clamp(pageSize, 1, 10_001);
        var page = Math.Clamp(pageNumber, 1, Math.Max(1, (int)Math.Ceiling(total / (double)size)));
        var documents = await query.Include(x => x.Product).ThenInclude(x => x.BaseUnit)
            .Include(x => x.WipLocation).Include(x => x.ResponsibleUser).Include(x => x.Applications)
            .OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        var rows = new List<WipInventoryRow>();
        foreach (var document in documents)
        {
            var effective = WipDocumentService.Effective(document);
            decimal Applied(WipDocumentApplicationKind kind) => effective.Where(x => x.Kind == kind).Sum(x => x.Quantity);
            var date = await warehouseClock.ConvertAsync(document.OccurredAt, cancellationToken);
            rows.Add(new(document.WipLocationId, document.WipLocation.Code, document.ProductId, document.Product.Sku,
                document.Product.Description, document.Product.BaseUnit.Code, WipDocumentService.Remaining(document),
                DateOnly.FromDateTime(date.DateTime), date, document.Id, document.IsOpening, document.Quantity,
                Applied(WipDocumentApplicationKind.Consumption), Applied(WipDocumentApplicationKind.Scrap),
                Applied(WipDocumentApplicationKind.WarehouseReturn), Applied(WipDocumentApplicationKind.SupplierReturn),
                document.ResponsibleUser.FullName, document.MovementLineId));
        }
        return new(rows, [], total, page, size);
    }

    public async Task<IReadOnlyList<WipIssueRow>> GetRecentIssuesAsync(
        Guid wipAreaId,
        int take = 10,
        CancellationToken cancellationToken = default)
        => await GetRecentIssuesAsync([wipAreaId], take, cancellationToken);

    public async Task<IReadOnlyList<WipIssueRow>> GetRecentIssuesAsync(
        IReadOnlyCollection<Guid> wipAreaIds,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        var ids = wipAreaIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
            return [];

        var rows = (await LoadRowsAsync(
                new(null, null),
                wipAreaIds: ids,
                cancellationToken: cancellationToken))
            .OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.MovementId)
            .Take(Math.Clamp(take, 1, 100));
        var localized = new List<WipIssueRow>();
        foreach (var row in rows)
            localized.Add(row with
            {
                OccurredAt = await warehouseClock.ConvertAsync(row.OccurredAt, cancellationToken)
            });
        return localized;
    }

    public async Task<IReadOnlyList<WipIssueRow>> SearchIssuesAsync(
        string? search,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var rows = (await LoadRowsAsync(new(null, null, search), cancellationToken: cancellationToken))
            .Where(item => item.Returnable > 0)
            .OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.MovementId)
            .Take(Math.Clamp(take, 1, 100));
        var localized = new List<WipIssueRow>();
        foreach (var row in rows)
            localized.Add(row with { OccurredAt = await warehouseClock.ConvertAsync(row.OccurredAt, cancellationToken) });
        return localized;
    }

    public async Task<WipIssueRow?> GetIssueAsync(Guid movementLineId, CancellationToken cancellationToken = default)
    {
        var row = (await LoadRowsAsync(new(null, null), movementLineId,
            cancellationToken: cancellationToken)).SingleOrDefault();
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
        var all = (await LoadRowsAsync(filter, cancellationToken: cancellationToken))
            .OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.MovementId);
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
        var rows = (await LoadRowsAsync(filter, cancellationToken: cancellationToken))
            .OrderByDescending(item => item.OccurredAt)
            .ThenBy(item => item.ProductSku, StringComparer.Ordinal);
        var localized = new List<WipIssueRow>();
        foreach (var row in rows)
            localized.Add(row with { OccurredAt = await warehouseClock.ConvertAsync(row.OccurredAt, cancellationToken) });
        return localized;
    }

    private async Task<IReadOnlyList<WipIssueRow>> LoadRowsAsync(
        WipReportFilter filter,
        Guid? movementLineId = null,
        IReadOnlyCollection<Guid>? wipAreaIds = null,
        CancellationToken cancellationToken = default)
    {
        var issueRows = await Query(filter, movementLineId, wipAreaIds).ToListAsync(cancellationToken);
        if (issueRows.Count == 0)
            return [];

        var lineIds = issueRows.Select(item => item.MovementLineId).ToArray();
        var dispositions = await dbContext.WipDispositions.AsNoTracking()
            .Where(disposition => lineIds.Contains(disposition.OriginalMovementLineId))
            .Select(disposition => new WipDispositionAmount(
                disposition.Id,
                disposition.OriginalMovementLineId,
                disposition.Type,
                disposition.Quantity,
                disposition.ReversesDispositionId))
            .ToListAsync(cancellationToken);
        var reversedDispositionIds = dispositions
            .Where(disposition => disposition.ReversesDispositionId is not null)
            .Select(disposition => disposition.ReversesDispositionId!.Value)
            .ToHashSet();
        var totalsByLine = dispositions
            .Where(disposition => disposition.ReversesDispositionId is null &&
                !reversedDispositionIds.Contains(disposition.Id))
            .GroupBy(disposition => disposition.OriginalMovementLineId)
            .ToDictionary(group => group.Key, group => new WipDispositionTotals(
                group.Where(item => item.Type == WipDispositionType.WarehouseReturn).Sum(item => item.Quantity),
                group.Where(item => item.Type == WipDispositionType.SupplierReturn).Sum(item => item.Quantity)));

        var rows = issueRows.Select(row =>
        {
            totalsByLine.TryGetValue(row.MovementLineId, out var totals);
            return new WipIssueRow(
                row.MovementId,
                row.MovementLineId,
                row.ProductId,
                row.OccurredAt,
                row.ProductSku,
                row.ProductDescription,
                row.Unit,
                row.SourceLocation,
                row.WipAreaId,
                row.WipArea,
                row.Issued,
                totals?.WarehouseReturned ?? 0m,
                totals?.SupplierReturned ?? 0m,
                row.Responsible,
                row.Reference,
                row.Notes);
        });
        if (filter.AgedBefore is not null)
            rows = rows.Where(row => row.OccurredAt <= filter.AgedBefore);
        if (filter.RequireNoEffectiveReturn)
            rows = rows.Where(row => row.WarehouseReturned == 0m && row.SupplierReturned == 0m);
        return rows.ToArray();
    }

    private IQueryable<WipIssueBaseRow> Query(WipReportFilter filter, Guid? movementLineId,
        IReadOnlyCollection<Guid>? wipAreaIds)
    {
        var query = dbContext.InventoryMovementLines.AsNoTracking()
            .Where(line => (line.Movement.Type == InventoryMovementType.Exit ||
                    line.Movement.Type == InventoryMovementType.Transfer) &&
                line.Movement.Purpose == InventoryMovementPurpose.ProductionIssue &&
                line.Movement.OperationalAreaId != null &&
                !dbContext.InventoryMovementCorrections.Any(correction => correction.OriginalMovementId == line.MovementId));
        if (movementLineId is not null) query = query.Where(line => line.Id == movementLineId);
        if (filter.From is not null) query = query.Where(line => line.Movement.OccurredAt >= filter.From);
        if (filter.To is not null) query = query.Where(line => line.Movement.OccurredAt < filter.To);
        if (filter.WipAreaId is not null) query = query.Where(line => line.Movement.OperationalAreaId == filter.WipAreaId);
        if (wipAreaIds is { Count: > 0 })
            query = query.Where(line => line.Movement.OperationalAreaId != null &&
                wipAreaIds.Contains(line.Movement.OperationalAreaId.Value));
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

        return query.Select(line => new WipIssueBaseRow(
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
            line.Movement.ResponsibleUser.FullName,
            line.Movement.Reference,
            line.Movement.Notes));
    }

    private sealed record WipIssueBaseRow(
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
        string Responsible,
        string? Reference,
        string? Notes);

    private sealed record WipDispositionAmount(
        Guid Id,
        Guid OriginalMovementLineId,
        WipDispositionType Type,
        decimal Quantity,
        Guid? ReversesDispositionId);

    private sealed record WipDispositionTotals(decimal WarehouseReturned, decimal SupplierReturned);

    private static string ClassifyActivity(
        InventoryMovementPurpose purpose,
        InventoryMovementType type,
        decimal delta) => purpose switch
        {
            InventoryMovementPurpose.ProductionIssue => "Recibido",
            InventoryMovementPurpose.WipConsumption => "Consumo registrado",
            InventoryMovementPurpose.WipWarehouseReturn => "Regreso a bodega",
            InventoryMovementPurpose.WipSupplierReturn => "Devolución a proveedor",
            InventoryMovementPurpose.CycleCountAdjustment => "Ajuste",
            _ when type == InventoryMovementType.Adjustment => "Ajuste",
            _ when delta > 0 => "Movimiento normal recibido",
            _ => "Movimiento normal enviado"
        };
}
