using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed record ProductionReportFilter(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Search = null,
    Guid? ProductId = null,
    Guid? StageId = null,
    Guid? ShiftId = null,
    ProductionWorkOrderStatus? Status = null,
    bool AlertsOnly = false,
    int Page = 1,
    int PageSize = 25);

public sealed record ProductionReportSummary(
    int Orders,
    int OpenOrders,
    int OverdueOrders,
    int OrdersWithAlerts,
    int OrdersWithOpenRework,
    int OrdersWithReceipt);

public sealed record ProductionOrderReportRow(
    Guid Id,
    string Number,
    string Sku,
    string? Description,
    string Unit,
    decimal OriginalTarget,
    decimal Target,
    decimal Received,
    ProductionWorkOrderStatus Status,
    DateOnly? DueDate,
    bool IsOverdue,
    int AlertCount,
    DateTimeOffset? LastActivityAt);

public sealed record ProductionMaterialReportRow(
    Guid PlanId,
    Guid OrderId,
    string OrderNumber,
    string Stage,
    string Sku,
    string? Description,
    string Unit,
    decimal OriginalPlan,
    decimal AuthorizedPlan,
    decimal Issued,
    decimal Consumed,
    decimal Returned,
    decimal Scrapped,
    decimal PendingSupply,
    decimal PlanVariance,
    decimal? TheoreticalConsumption,
    decimal? FirstPassConsumption,
    decimal? TechnicalVariance);

public sealed record ProductionReworkReportRow(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    string Stage,
    string Unit,
    decimal Initial,
    decimal Recovered,
    decimal Discarded,
    decimal Pending,
    int Attempts,
    DateTimeOffset OriginAt,
    int AgeHours,
    int? AlertHours,
    bool IsLate);

public sealed record ProductionRecordReportRow(
    Guid Id,
    Guid OrderId,
    string OrderNumber,
    string Type,
    string? Stage,
    string? Shift,
    string Responsible,
    decimal Quantity,
    decimal Good,
    decimal Rework,
    decimal Scrap,
    string? Reason,
    DateTimeOffset RecordedAt,
    bool IsEffective);

public sealed record ProductionReportPage(
    DateTimeOffset GeneratedAtLocal,
    string TimeZoneId,
    string PeriodLabel,
    ProductionReportSummary Summary,
    IReadOnlyList<ProductionOrderReportRow> Orders,
    IReadOnlyList<ProductionMaterialReportRow> Materials,
    IReadOnlyList<ProductionReworkReportRow> Rework,
    IReadOnlyList<ProductionRecordReportRow> Records,
    int TotalCount,
    int Page,
    int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (decimal)PageSize));
}

public sealed class ProductionReportService(
    WarehouseDbContext db,
    WarehouseSettingsService settings,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProductionReportPage> GetAsync(
        string view,
        ProductionReportFilter filter,
        string periodLabel,
        CancellationToken token = default)
    {
        var warehouseSettings = await settings.GetAsync(token);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(warehouseSettings.TimeZoneId);
        var now = _timeProvider.GetUtcNow();
        var generatedAtLocal = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(generatedAtLocal.DateTime);
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 10, ProductionReportExportService.RowLimit);
        filter = filter with { Page = page, PageSize = pageSize, Search = Normalize(filter.Search) };

        var ordersQuery = ApplyOrderFilter(db.ProductionWorkOrders.AsNoTracking(), filter);
        var summary = await BuildSummaryAsync(ordersQuery, today, token);

        IReadOnlyList<ProductionOrderReportRow> orders = [];
        IReadOnlyList<ProductionMaterialReportRow> materials = [];
        IReadOnlyList<ProductionReworkReportRow> rework = [];
        IReadOnlyList<ProductionRecordReportRow> records = [];
        int total;

        switch (view)
        {
            case "materials":
                (materials, total) = await GetMaterialsAsync(ordersQuery, filter, page, pageSize, token);
                break;
            case "rework":
                (rework, total) = await GetReworkAsync(ordersQuery, filter, page, pageSize, now, token);
                break;
            case "records":
                (records, total) = await GetRecordsAsync(ordersQuery, filter, page, pageSize, token);
                break;
            default:
                (orders, total) = await GetOrdersAsync(ordersQuery, filter, page, pageSize, today, token);
                break;
        }

        return new(generatedAtLocal, zone.Id, periodLabel, summary, orders, materials, rework, records,
            total, page, pageSize);
    }

    private static IQueryable<ProductionWorkOrder> ApplyOrderFilter(
        IQueryable<ProductionWorkOrder> query,
        ProductionReportFilter filter)
    {
        if (filter.FromUtc.HasValue && filter.ToUtc.HasValue)
            query = query.Where(x => x.CreatedAt >= filter.FromUtc && x.CreatedAt < filter.ToUtc
                || x.Events.Any(e => e.RecordedAt >= filter.FromUtc && e.RecordedAt < filter.ToUtc));
        else if (filter.FromUtc.HasValue)
            query = query.Where(x => x.CreatedAt >= filter.FromUtc || x.Events.Any(e => e.RecordedAt >= filter.FromUtc));
        else if (filter.ToUtc.HasValue)
            query = query.Where(x => x.CreatedAt < filter.ToUtc || x.Events.Any(e => e.RecordedAt < filter.ToUtc));
        if (filter.ProductId.HasValue) query = query.Where(x => x.ProductId == filter.ProductId);
        if (filter.StageId.HasValue) query = query.Where(x => x.Stages.Any(s => s.SourceStageId == filter.StageId));
        if (filter.ShiftId.HasValue) query = query.Where(x => x.Events.Any(e => e.ShiftId == filter.ShiftId));
        if (filter.Status.HasValue) query = query.Where(x => x.Status == filter.Status);
        if (filter.Search is not null)
        {
            var term = filter.Search.ToUpper();
            query = query.Where(x => x.Number.ToUpper().Contains(term)
                || x.ExternalReference != null && x.ExternalReference.ToUpper().Contains(term)
                || x.Product.Sku.ToUpper().Contains(term)
                || x.Product.Description != null && x.Product.Description.ToUpper().Contains(term));
        }
        return query;
    }

    private async Task<ProductionReportSummary> BuildSummaryAsync(
        IQueryable<ProductionWorkOrder> query,
        DateOnly today,
        CancellationToken token)
    {
        var rows = await query.Select(x => new
        {
            x.Id,
            x.Status,
            x.DueDate,
            x.TargetQuantity,
            Received = x.Events.Where(e => e.Type == ProductionEventType.WarehouseReceived).Sum(e => e.Quantity)
        }).ToListAsync(token);
        var overdue = rows.Count(x => x.DueDate < today && x.Received < x.TargetQuantity
            && x.Status is not (ProductionWorkOrderStatus.Cancelled or ProductionWorkOrderStatus.Draft));
        var attention = await ProductionAttentionQuery.Query(db, query, _timeProvider.GetUtcNow(), today).ToListAsync(token);
        var alerts = attention.Count(x => x.Count > 0);
        return new(rows.Count,
            rows.Count(x => x.Status is ProductionWorkOrderStatus.Released or ProductionWorkOrderStatus.InProgress
                or ProductionWorkOrderStatus.Paused or ProductionWorkOrderStatus.PrincipalClosed),
            overdue, alerts, attention.Count(x => x.ReworkCases > 0), rows.Count(x => x.Received > 0));
    }

    private async Task<(IReadOnlyList<ProductionOrderReportRow>, int)> GetOrdersAsync(
        IQueryable<ProductionWorkOrder> query,
        ProductionReportFilter filter,
        int page,
        int pageSize,
        DateOnly today,
        CancellationToken token)
    {
        var attention = ProductionAttentionQuery.Query(db, query, _timeProvider.GetUtcNow(), today);
        if (filter.AlertsOnly) { var ids = attention.Where(x => x.SupplyProblems + x.PendingLines + x.ReworkCases + x.Overdue + x.Inactive > 0).Select(x => x.Id); query = query.Where(x => ids.Contains(x.Id)); }
        var total = await query.CountAsync(token);
        var rows = await query
            .OrderBy(x => x.DueDate == null).ThenBy(x => x.DueDate).ThenByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.Number,
                x.Product.Sku,
                x.Product.Description,
                Unit = x.Unit.Code,
                OriginalTarget = x.OriginalTargetQuantity,
                Target = x.TargetQuantity,
                x.Status,
                x.DueDate,
                Received = x.Events.Where(e => e.Type == ProductionEventType.WarehouseReceived).Sum(e => e.Quantity),
                LastActivity = x.Events.Max(e => (DateTimeOffset?)e.RecordedAt)
            }).ToListAsync(token);
        var rowIds = rows.Select(x => x.Id).ToArray();
        var reasons = await ProductionAttentionQuery.Query(db, query.Where(x => rowIds.Contains(x.Id)), _timeProvider.GetUtcNow(), today).ToDictionaryAsync(x => x.Id, token);
        return (rows.Select(x =>
        {
            var overdue = x.DueDate < today && x.Received < x.Target
                && x.Status is not (ProductionWorkOrderStatus.Cancelled or ProductionWorkOrderStatus.Draft);
            return new ProductionOrderReportRow(x.Id, x.Number, x.Sku, x.Description, x.Unit, x.OriginalTarget,
                x.Target, x.Received, x.Status, x.DueDate, overdue,
                reasons[x.Id].Count, x.LastActivity);
        }).ToArray(), total);
    }

    private async Task<(IReadOnlyList<ProductionMaterialReportRow>, int)> GetMaterialsAsync(
        IQueryable<ProductionWorkOrder> orders,
        ProductionReportFilter filter,
        int page,
        int pageSize,
        CancellationToken token)
    {
        var query = db.ProductionOrderMaterialPlans.AsNoTracking().Where(x => orders.Any(o => o.Id == x.WorkOrderId));
        if (filter.StageId.HasValue) query = query.Where(x => x.WorkOrderStage.SourceStageId == filter.StageId);
        var total = await query.CountAsync(token);
        var rows = await query.OrderBy(x => x.WorkOrder.Number).ThenBy(x => x.WorkOrderStage.Sequence)
            .ThenBy(x => x.MaterialProduct.Sku).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new ProductionMaterialReportRow(
                x.Id, x.WorkOrderId, x.WorkOrder.Number, x.WorkOrderStage.Name, x.MaterialProduct.Sku,
                x.MaterialProduct.Description, x.Unit.Code, x.OriginalPlannedQuantity, x.PlannedQuantity,
                x.WorkOrder.MaterialIssues.Where(i => i.ProductId == x.MaterialProductId && i.WorkOrderStageId == x.WorkOrderStageId)
                    .Sum(i => i.Quantity - i.CancelledQuantity),
                x.WorkOrder.MaterialOperations.Where(o => o.WorkOrderStageId == x.WorkOrderStageId
                    && o.Type == ProductionMaterialOperationType.Consumption
                    && !x.WorkOrder.MaterialOperations.Any(r => r.Type == ProductionMaterialOperationType.Reversal && r.ReversesOperationId == o.Id))
                    .SelectMany(o => o.Lines).Where(l => l.IssueLink.ProductId == x.MaterialProductId).Sum(l => l.Quantity),
                x.WorkOrder.MaterialOperations.Where(o => o.WorkOrderStageId == x.WorkOrderStageId
                    && (o.Type == ProductionMaterialOperationType.WarehouseReturn || o.Type == ProductionMaterialOperationType.SupplierReturn)
                    && !x.WorkOrder.MaterialOperations.Any(r => r.Type == ProductionMaterialOperationType.Reversal && r.ReversesOperationId == o.Id))
                    .SelectMany(o => o.Lines).Where(l => l.IssueLink.ProductId == x.MaterialProductId).Sum(l => l.Quantity),
                x.WorkOrder.MaterialOperations.Where(o => o.WorkOrderStageId == x.WorkOrderStageId
                    && o.Type == ProductionMaterialOperationType.Scrap
                    && !x.WorkOrder.MaterialOperations.Any(r => r.Type == ProductionMaterialOperationType.Reversal && r.ReversesOperationId == o.Id))
                    .SelectMany(o => o.Lines).Where(l => l.IssueLink.ProductId == x.MaterialProductId).Sum(l => l.Quantity),
                x.WorkOrder.SupplyRequests.SelectMany(r => r.Lines).Where(l => l.MaterialPlanId == x.Id)
                    .Sum(l => l.RequiredQuantity + l.ReopenedQuantity - l.CancelledQuantity
                        - l.IssueLinks.Sum(i => i.Quantity - i.CancelledQuantity)),
                0,
                x.WorkOrder.OriginalTargetQuantity > 0
                    ? x.OriginalPlannedQuantity / x.WorkOrder.OriginalTargetQuantity
                        * x.WorkOrder.Batches.SelectMany(b => b.Results)
                            .Where(r => r.WorkOrderStageId == x.WorkOrderStageId && !r.IsRework
                                && !x.WorkOrder.Events.Any(e => e.Type == ProductionEventType.ResultReversed
                                    && e.RelatedEvent!.OperationId == r.OperationId))
                            .Sum(r => r.InputQuantity)
                    : null,
                x.WorkOrder.OriginalTargetQuantity > 0
                    ? x.WorkOrder.Batches.SelectMany(b => b.Results)
                        .Where(r => r.WorkOrderStageId == x.WorkOrderStageId && !r.IsRework
                            && !x.WorkOrder.Events.Any(e => e.Type == ProductionEventType.ResultReversed
                                && e.RelatedEvent!.OperationId == r.OperationId))
                        .SelectMany(r => r.Materials).Where(m => m.IssueLink.ProductId == x.MaterialProductId)
                        .Sum(m => m.Quantity)
                    : null,
                null)).ToListAsync(token);
        return (rows.Select(x => x with
        {
            PlanVariance = x.Consumed - x.AuthorizedPlan,
            TechnicalVariance = x.TheoreticalConsumption.HasValue && x.FirstPassConsumption.HasValue
                ? x.FirstPassConsumption.Value - x.TheoreticalConsumption.Value : null
        }).ToArray(), total);
    }

    private async Task<(IReadOnlyList<ProductionReworkReportRow>, int)> GetReworkAsync(
        IQueryable<ProductionWorkOrder> orders,
        ProductionReportFilter filter,
        int page,
        int pageSize,
        DateTimeOffset now,
        CancellationToken token)
    {
        var query = db.ProductionReworkCases.AsNoTracking().Where(x => orders.Any(o => o.Id == x.WorkOrderId));
        if (filter.StageId.HasValue) query = query.Where(x => x.WorkOrderStage.SourceStageId == filter.StageId);
        var total = await query.CountAsync(token);
        var rows = await query.OrderBy(x => x.OriginAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.WorkOrderId,
                x.WorkOrder.Number,
                Stage = x.WorkOrderStage.Name,
                Unit = x.WorkOrder.Unit.Code,
                Initial = x.InitialQuantity,
                x.OriginAt,
                AlertHours = x.WorkOrderStage.SourceStage.ReworkAlertHours,
                Recovered = x.Attempts.Where(a => !x.WorkOrder.Events.Any(e => e.Type == ProductionEventType.ResultReversed
                    && e.RelatedEvent!.OperationId == a.Result.OperationId)).Sum(a => a.Result.GoodQuantity),
                Discarded = x.Attempts.Where(a => !x.WorkOrder.Events.Any(e => e.Type == ProductionEventType.ResultReversed
                    && e.RelatedEvent!.OperationId == a.Result.OperationId)).Sum(a => a.Result.ScrapQuantity),
                Attempts = x.Attempts.Count(a => !x.WorkOrder.Events.Any(e => e.Type == ProductionEventType.ResultReversed
                    && e.RelatedEvent!.OperationId == a.Result.OperationId))
            }).ToListAsync(token);
        var output = rows.Select(x =>
        {
            var pending = Math.Max(0, x.Initial - x.Recovered - x.Discarded);
            var age = Math.Max(0, (int)Math.Floor((now - x.OriginAt).TotalHours));
            return new ProductionReworkReportRow(x.Id, x.WorkOrderId, x.Number, x.Stage, x.Unit, x.Initial,
                x.Recovered, x.Discarded, pending, x.Attempts, x.OriginAt, age, x.AlertHours,
                pending > 0 && x.AlertHours.HasValue && age >= x.AlertHours.Value);
        }).Where(x => !filter.AlertsOnly || x.IsLate).ToArray();
        return (output, total);
    }

    private async Task<(IReadOnlyList<ProductionRecordReportRow>, int)> GetRecordsAsync(
        IQueryable<ProductionWorkOrder> orders,
        ProductionReportFilter filter,
        int page,
        int pageSize,
        CancellationToken token)
    {
        var query = db.ProductionEvents.AsNoTracking().Where(x => orders.Any(o => o.Id == x.WorkOrderId));
        if (filter.FromUtc.HasValue) query = query.Where(x => x.RecordedAt >= filter.FromUtc);
        if (filter.ToUtc.HasValue) query = query.Where(x => x.RecordedAt < filter.ToUtc);
        if (filter.StageId.HasValue) query = query.Where(x => x.WorkOrderStage!.SourceStageId == filter.StageId);
        if (filter.ShiftId.HasValue) query = query.Where(x => x.ShiftId == filter.ShiftId);
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(x => x.RecordedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new ProductionRecordReportRow(x.Id, x.WorkOrderId, x.WorkOrder.Number, x.Type.ToString(),
                x.WorkOrderStage != null ? x.WorkOrderStage.Name : null, x.Shift != null ? x.Shift.Name : null,
                x.ResponsibleUser.FullName, x.Quantity, x.GoodQuantity, x.ReworkQuantity, x.ScrapQuantity,
                x.Reason, x.RecordedAt, x.Type != ProductionEventType.ResultReversed
                    && !x.WorkOrder.Events.Any(r => r.Type == ProductionEventType.ResultReversed && r.RelatedEventId == x.Id)))
            .ToListAsync(token);
        return (rows, total);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
