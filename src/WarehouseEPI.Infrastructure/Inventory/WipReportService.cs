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
    DateTimeOffset UpdatedAt);

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
        var balances = dbContext.InventoryBalances.AsNoTracking()
            .Where(balance => balance.Location.OperationalRole == LocationOperationalRole.Wip &&
                balance.Quantity != 0);
        if (filter.WipAreaId is Guid wipAreaId)
            balances = balances.Where(balance => balance.LocationId == wipAreaId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            balances = balances.Where(balance => balance.Product.Sku.ToUpper().Contains(term) ||
                (balance.Product.Description != null && balance.Product.Description.ToUpper().Contains(term)) ||
                balance.Location.Code.ToUpper().Contains(term));
        }
        var balanceRows = await balances.Select(balance => new
        {
            balance.LocationId,
            WipArea = balance.Location.Code,
            balance.ProductId,
            ProductSku = balance.Product.Sku,
            ProductDescription = balance.Product.Description,
            Unit = balance.Product.BaseUnit.Code,
            balance.Quantity,
            LotDate = balance.Lot == null ? null : balance.Lot.LotDate,
            balance.UpdatedAt
        })
            .ToListAsync(cancellationToken);
        var inventoryUtc = balanceRows
            .GroupBy(balance => new
            {
                balance.LocationId,
                balance.WipArea,
                balance.ProductId,
                balance.ProductSku,
                balance.ProductDescription,
                balance.Unit
            })
            .Select(group => new WipInventoryRow(
                group.Key.LocationId, group.Key.WipArea, group.Key.ProductId, group.Key.ProductSku,
                group.Key.ProductDescription, group.Key.Unit, group.Sum(item => item.Quantity),
                group.Where(item => item.Quantity > 0).Min(item => item.LotDate),
                group.Max(item => item.UpdatedAt)))
            .Where(item => item.Quantity != 0);
        if (filter.AgedBefore is DateTimeOffset agedBefore)
        {
            var cutoff = DateOnly.FromDateTime(agedBefore.UtcDateTime);
            inventoryUtc = inventoryUtc.Where(item => item.Quantity > 0 &&
                item.OldestPositiveLotDate is not null && item.OldestPositiveLotDate <= cutoff);
        }
        var orderedInventory = inventoryUtc.OrderBy(item => item.WipArea).ThenBy(item => item.ProductSku).ToArray();
        var inventory = new List<WipInventoryRow>(orderedInventory.Length);
        foreach (var row in orderedInventory)
            inventory.Add(row with { UpdatedAt = await warehouseClock.ConvertAsync(row.UpdatedAt, cancellationToken) });

        var effectiveMovementIds = dbContext.InventoryMovements.AsNoTracking()
            .WhereEffective(dbContext)
            .Select(movement => movement.Id);
        var changes = dbContext.InventoryBalanceChanges.AsNoTracking()
            .Where(change => change.Location.OperationalRole == LocationOperationalRole.Wip &&
                effectiveMovementIds.Contains(change.MovementLine.MovementId));
        if (filter.From is DateTimeOffset from)
            changes = changes.Where(change => change.MovementLine.Movement.OccurredAt >= from);
        if (filter.To is DateTimeOffset to)
            changes = changes.Where(change => change.MovementLine.Movement.OccurredAt < to);
        if (filter.WipAreaId is Guid activityWipAreaId)
            changes = changes.Where(change => change.LocationId == activityWipAreaId);
        if (filter.ResponsibleUserId is Guid responsibleUserId)
            changes = changes.Where(change => change.MovementLine.Movement.ResponsibleUserId == responsibleUserId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            changes = changes.Where(change => change.MovementLine.Product.Sku.ToUpper().Contains(term) ||
                (change.MovementLine.Product.Description != null && change.MovementLine.Product.Description.ToUpper().Contains(term)) ||
                change.Location.Code.ToUpper().Contains(term) ||
                (change.MovementLine.Movement.Reference != null && change.MovementLine.Movement.Reference.ToUpper().Contains(term)) ||
                (change.MovementLine.SourceLocation != null && change.MovementLine.SourceLocation.Code.ToUpper().Contains(term)) ||
                (change.MovementLine.DestinationLocation != null && change.MovementLine.DestinationLocation.Code.ToUpper().Contains(term)));
        }

        var page = Math.Max(1, pageNumber);
        var size = Math.Clamp(pageSize, 1, 10_001);
        var total = await changes.CountAsync(cancellationToken);
        var rawActivity = await changes
            .OrderByDescending(change => change.MovementLine.Movement.OccurredAt)
            .ThenByDescending(change => change.MovementLine.MovementId)
            .ThenBy(change => change.MovementLine.LineNumber)
            .ThenBy(change => change.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(change => new
            {
                change.MovementLine.MovementId,
                change.MovementLine.Movement.OccurredAt,
                change.LocationId,
                WipArea = change.Location.Code,
                change.MovementLine.ProductId,
                ProductSku = change.MovementLine.Product.Sku,
                ProductDescription = change.MovementLine.Product.Description,
                Unit = change.MovementLine.Unit.Code,
                change.MovementLine.Movement.Type,
                change.MovementLine.Movement.Purpose,
                SourceLocation = change.MovementLine.SourceLocation == null ? null : change.MovementLine.SourceLocation.Code,
                DestinationLocation = change.MovementLine.DestinationLocation == null ? null : change.MovementLine.DestinationLocation.Code,
                Delta = change.DeltaQuantity,
                Responsible = change.MovementLine.Movement.ResponsibleUser.FullName,
                change.MovementLine.Movement.Reference,
                change.MovementLine.Movement.Notes
            })
            .ToListAsync(cancellationToken);
        var activity = new List<WipActivityRow>(rawActivity.Count);
        foreach (var row in rawActivity)
        {
            var occurredAt = await warehouseClock.ConvertAsync(row.OccurredAt, cancellationToken);
            activity.Add(new(
                row.MovementId, occurredAt, row.LocationId, row.WipArea, row.ProductId,
                row.ProductSku, row.ProductDescription, row.Unit,
                ClassifyActivity(row.Purpose, row.Type, row.Delta),
                row.SourceLocation, row.DestinationLocation, row.Delta,
                row.Responsible, row.Reference, row.Notes));
        }
        return new(inventory, activity, total, page, size);
    }

    public async Task<IReadOnlyList<WipIssueRow>> GetRecentIssuesAsync(
        Guid wipAreaId,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        if (wipAreaId == Guid.Empty)
            return [];

        var rows = (await LoadRowsAsync(
                new(null, null, WipAreaId: wipAreaId),
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
        var row = (await LoadRowsAsync(new(null, null), movementLineId, cancellationToken)).SingleOrDefault();
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
        CancellationToken cancellationToken = default)
    {
        var issueRows = await Query(filter, movementLineId).ToListAsync(cancellationToken);
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

    private IQueryable<WipIssueBaseRow> Query(WipReportFilter filter, Guid? movementLineId)
    {
        var query = dbContext.InventoryMovementLines.AsNoTracking()
            .Where(line => line.Movement.Type == InventoryMovementType.Exit &&
                line.Movement.Purpose == InventoryMovementPurpose.ProductionIssue &&
                line.Movement.OperationalAreaId != null &&
                !dbContext.InventoryMovementCorrections.Any(correction => correction.OriginalMovementId == line.MovementId));
        if (movementLineId is not null) query = query.Where(line => line.Id == movementLineId);
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
