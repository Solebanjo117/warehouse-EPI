using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Production;

public sealed class ProductionQueryService(WarehouseDbContext db, WarehouseClock? warehouseClock = null, TimeProvider? timeProvider = null)
{
    public async Task<ProductionReceiptLink?> GetReceiptLinkAsync(Guid movementId, CancellationToken token = default)
    {
        var link = await db.ProductionEvents.AsNoTracking().Include(x => x.WorkOrder).Include(x => x.Batch)
            .SingleOrDefaultAsync(x => x.InventoryMovementId == movementId && x.Type == ProductionEventType.WarehouseReceived, token);
        if (link is null) return null;
        var progress = link.BatchId is Guid batchId ? await GetBatchProgressAsync(link.WorkOrderId, batchId, token) : (await GetAsync(link.WorkOrderId, token))?.Stages ?? [];
        var received = await db.ProductionEvents.Where(x => x.WorkOrderId == link.WorkOrderId && x.BatchId == link.BatchId && x.Type == ProductionEventType.WarehouseReceived).SumAsync(x => x.Quantity, token);
        return new(link.WorkOrderId, link.WorkOrder.Number, link.BatchId, link.Batch?.Number, link.Quantity, received,
            Math.Max(0, progress.OrderByDescending(x => x.Sequence).FirstOrDefault()?.AvailableToDeliver ?? 0), ProductionActionPolicy.Allows(link.WorkOrder.Status, "capture"));
    }

    public async Task<IReadOnlyList<ProductionOrderRow>> GetOrdersAsync(string? search = null, ProductionWorkOrderStatus? status = null, int take = 100, CancellationToken token = default)
    {
        var q = db.ProductionWorkOrders.AsNoTracking().Include(x => x.Product).ThenInclude(x => x.BaseUnit).Include(x => x.Events).AsQueryable();
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (search is not null) q = q.Where(x => x.Number.Contains(search) || (x.ExternalReference != null && x.ExternalReference.Contains(search)) || x.Product.Sku.Contains(search) || x.Batches.Any(b => b.Number.Contains(search) || b.FinishedProductLot.Number.Contains(search)));
        if (status.HasValue) q = q.Where(x => x.Status == status);
        return await Project(q.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 200))).ToListAsync(token);
    }

    public async Task<ProductionOrderPage> SearchOrdersAsync(ProductionOrderSearch search, CancellationToken token = default)
    {
        var q = await FilterOrdersAsync(search, token);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow(); var today = warehouseClock is null ? DateOnly.FromDateTime(now.UtcDateTime) : await warehouseClock.GetDateAsync(now, token);
        var size = Math.Clamp(search.PageSize, 1, 100); var count = await q.CountAsync(token); var page = Math.Clamp(search.Page, 1, Math.Max(1, (count + size - 1) / size));
        var selected = q.OrderByDescending(x => x.SupplyRequests.Any(r => r.Lines.Any(l => l.RequiredQuantity + l.ReopenedQuantity - l.CancelledQuantity > l.IssueLinks.Sum(i => i.Quantity - i.CancelledQuantity))) ? x.SupplyPriority : ProductionSupplyPriority.Normal).ThenBy(x => x.DueDate == null).ThenBy(x => x.DueDate).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * size).Take(size);
        var raw = await Project(selected).ToListAsync(token); var selectedIds = raw.Select(x => x.Id).ToArray();
        var reasons = await ProductionAttentionQuery.Query(db, db.ProductionWorkOrders.Where(x => selectedIds.Contains(x.Id)), now, today).ToDictionaryAsync(x => x.Id, token);
        var pageOrders = await db.ProductionWorkOrders.AsNoTracking().Where(x => selectedIds.Contains(x.Id)).Include(x => x.Stages).Include(x => x.Events).Include(x => x.Batches).ToDictionaryAsync(x => x.Id, token);
        var items = raw.Select(x =>
        {
            var order = pageOrders[x.Id]; var progress = ProductionService.BuildProgress(order);
            var pending = progress.Where(p => p.AvailableInput > 0 || p.PendingReceipt > 0 || p.AvailableToDeliver > 0 || p.Rework > 0).OrderBy(p => p.Sequence).ToArray();
            var next = x.Status switch
            {
                ProductionWorkOrderStatus.Paused => "Reanudar orden · NIP ADMIN",
                ProductionWorkOrderStatus.Closed => "Consultar o reabrir orden",
                ProductionWorkOrderStatus.Cancelled => "Consultar orden cancelada",
                ProductionWorkOrderStatus.Draft => "Revisar planificación",
                _ => progress.Any(p => p.PendingReceipt > 0) ? "Recibir entrega pendiente" : progress.Any(p => p.AvailableToDeliver > 0) ? "Entregar producto bueno" : progress.Any(p => p.Rework > 0) ? "Atender retrabajo" : order.UsesBatchTraceability && order.Batches.Count == 0 ? "Crear lote" : progress.Any(p => p.AvailableInput > 0) ? "Registrar avance" : reasons[x.Id].PendingLines > 0 ? "Revisar surtimientos" : "Revisar cierre"
            };
            return x with { AlertCount = reasons[x.Id].Count, Attention = reasons[x.Id].Description, NextAction = next, ActiveProcess = string.Join(", ", pending.Select(p => p.Name)) };
        }).ToArray();
        return new(items, page, size, count);
    }

    private async Task<IQueryable<ProductionWorkOrder>> FilterOrdersAsync(ProductionOrderSearch search, CancellationToken token)
    {
        var q = db.ProductionWorkOrders.AsNoTracking().AsQueryable();
        var term = string.IsNullOrWhiteSpace(search.Search) ? null : search.Search.Trim();
        if (term is not null) q = q.Where(x => x.Number.Contains(term) || (x.ExternalReference != null && x.ExternalReference.Contains(term)) ||
            x.Product.Sku.Contains(term) || x.Batches.Any(b => b.Number.Contains(term) || b.FinishedProductLot.Number.Contains(term)));
        if (search.Status.HasValue) q = q.Where(x => x.Status == search.Status);
        else q = search.View switch { "active" => q.Where(x => x.Status == ProductionWorkOrderStatus.Released || x.Status == ProductionWorkOrderStatus.InProgress || x.Status == ProductionWorkOrderStatus.Paused || x.Status == ProductionWorkOrderStatus.PrincipalClosed), "drafts" => q.Where(x => x.Status == ProductionWorkOrderStatus.Draft), "closed" => q.Where(x => x.Status == ProductionWorkOrderStatus.Closed), "cancelled" => q.Where(x => x.Status == ProductionWorkOrderStatus.Cancelled), _ => q };
        if (search.ActiveStageId is Guid stageId) q = ProductionAttentionQuery.WithPendingStage(q, stageId);
        if (warehouseClock is not null) { var interval = await warehouseClock.GetUtcIntervalAsync(search.From, search.To, token); if (interval.FromInclusive.HasValue) q = q.Where(x => x.CreatedAt >= interval.FromInclusive); if (interval.ToExclusive.HasValue) q = q.Where(x => x.CreatedAt < interval.ToExclusive); }
        else { if (search.From.HasValue) q = q.Where(x => x.CreatedAt >= search.From.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)); if (search.To.HasValue) q = q.Where(x => x.CreatedAt < search.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)); }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow(); var today = warehouseClock is null ? DateOnly.FromDateTime(now.UtcDateTime) : await warehouseClock.GetDateAsync(now, token);
        var attention = ProductionAttentionQuery.Query(db, q, now, today);
        if (search.AlertsOnly) { var ids = attention.Where(a => a.SupplyProblems + a.PendingLines + a.ReworkCases + a.Overdue + a.Inactive > 0).Select(a => a.Id); q = q.Where(x => ids.Contains(x.Id)); }
        return q;
    }
    public async Task<ProductionQueueSnapshot> GetSnapshotAsync(ProductionOrderSearch search, CancellationToken token = default)
    {
        var q = await FilterOrdersAsync(search, token);
        var rows = await q.OrderBy(x => x.Id).Select(x => new
        {
            x.Id,
            x.Version,
            x.Status,
            x.TargetQuantity,
            x.AuthorizedQuantity,
            x.DueDate,
            x.SupplyPriority,
            Events = x.Events.Count,
            EventAt = x.Events.Max(e => (DateTimeOffset?)e.RecordedAt),
            Requests = x.SupplyRequests.Sum(r => (long)r.Version),
            Problems = x.SupplyRequests.SelectMany(r => r.Events).Count(),
            Required = x.SupplyRequests.SelectMany(r => r.Lines).Sum(l => l.RequiredQuantity - l.CancelledQuantity + l.ReopenedQuantity),
            Delivered = x.SupplyRequests.SelectMany(r => r.Lines).SelectMany(l => l.IssueLinks).Sum(i => i.Quantity - i.CancelledQuantity)
        }).ToListAsync(token);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow(); var today = warehouseClock is null ? DateOnly.FromDateTime(now.UtcDateTime) : await warehouseClock.GetDateAsync(now, token);
        var alerts = await ProductionAttentionQuery.Query(db, q, now, today).OrderBy(x => x.Id).ToListAsync(token);
        var bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { rows, alerts }));
        return new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), (timeProvider ?? TimeProvider.System).GetUtcNow());
    }

    private static IQueryable<ProductionOrderRow> Project(IQueryable<ProductionWorkOrder> q) => q.Select(x => new ProductionOrderRow(
        x.Id, x.Number, x.ExternalReference, x.Product.Sku, x.Product.Description, x.TargetQuantity,
        x.Events.Where(e => e.Type == ProductionEventType.WarehouseReceived).Sum(e => e.Quantity), x.Product.BaseUnit.Code, x.Status, x.DueDate,
        x.Batches.SelectMany(b => b.Results).OrderByDescending(r => r.RecordedAt).Select(r => r.WorkOrderStage.Name).FirstOrDefault(),
        x.Events.Count(e => e.Type == ProductionEventType.Delivered && e.Quantity > x.Events.Where(r => r.RelatedEventId == e.Id && (r.Type == ProductionEventType.Received || r.Type == ProductionEventType.DifferenceReturned || r.Type == ProductionEventType.DifferenceLost)).Sum(r => r.Quantity)),
        x.UsesBatchTraceability, x.SupplyRequests.Any(r => r.Lines.Any(l => l.RequiredQuantity + l.ReopenedQuantity - l.CancelledQuantity > l.IssueLinks.Sum(i => i.Quantity - i.CancelledQuantity))) ? x.SupplyPriority : ProductionSupplyPriority.Normal, null, null));
    public async Task<ProductionOrderDetail?> GetAsync(Guid id, CancellationToken token = default)
    {
        var o = await db.ProductionWorkOrders.AsNoTracking().Include(x => x.Product).ThenInclude(x => x.BaseUnit).Include(x => x.Unit).Include(x => x.Stages).Include(x => x.Events).ThenInclude(x => x.ResponsibleUser).Include(x => x.Events).ThenInclude(x => x.Shift).SingleOrDefaultAsync(x => x.Id == id, token);
        if (o is null) return null; var names = o.Stages.ToDictionary(x => x.Id, x => x.Name); var progress = ProductionService.BuildProgress(o);
        var events = o.Events.OrderByDescending(x => x.RecordedAt).Select(x => new ProductionEventItem(x.Type, x.WorkOrderStageId is Guid s && names.TryGetValue(s, out var sn) ? sn : null, x.RelatedStageId is Guid r && names.TryGetValue(r, out var rn) ? rn : null, x.Quantity, x.GoodQuantity, x.ReworkQuantity, x.ScrapQuantity, x.ResponsibleUser.FullName, x.Shift?.Name, x.Reason, x.RecordedAt, x.InventoryMovementId)).ToArray();
        return new(o.Id, o.Number, o.ExternalReference, o.Product.Sku, o.Product.Description, o.Unit.Code, o.Unit.AllowsDecimals, o.TargetQuantity, o.AuthorizedQuantity, o.DueDate, o.Notes, o.Status, o.Version, o.Events.Where(x => x.Type == ProductionEventType.WarehouseReceived).Sum(x => x.Quantity), progress, events, o.UsesBatchTraceability, o.RecipeVersion);
    }
    public async Task<IReadOnlyList<ProductionStageProgress>> GetBatchProgressAsync(Guid orderId, Guid batchId, CancellationToken token = default)
    {
        var order = await db.ProductionWorkOrders.AsNoTracking().Include(x => x.Stages).Include(x => x.Events).SingleAsync(x => x.Id == orderId, token);
        var batch = await db.ProductionBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == batchId && x.WorkOrderId == orderId, token);
        if (batch is null) return [];
        order.AuthorizedQuantity = batch.AssignedQuantity;
        order.Events = order.Events.Where(x => x.BatchId == batchId).ToList();
        return ProductionService.BuildProgress(order);
    }
    public Task<List<ProductionStage>> GetStagesAsync(CancellationToken t = default) => db.ProductionStages.AsNoTracking().OrderBy(x => x.Name).ToListAsync(t);
    public Task<List<ProductionShift>> GetShiftsAsync(CancellationToken t = default) => db.ProductionShifts.AsNoTracking().OrderBy(x => x.Name).ToListAsync(t);
    public Task<List<Product>> GetProductsWithoutRouteAsync(CancellationToken t = default) => db.Products.AsNoTracking().Where(x => x.IsActive && !db.ProductionRoutes.Any(r => r.ProductId == x.Id && r.IsActive)).OrderBy(x => x.Sku).Take(2000).ToListAsync(t);
    public Task<List<Product>> GetRoutedProductsAsync(CancellationToken t = default) => db.Products.AsNoTracking().Where(x => x.IsActive && db.ProductionRoutes.Any(r => r.ProductId == x.Id && r.IsActive)).OrderBy(x => x.Sku).Take(2000).ToListAsync(t);
    public Task<List<Location>> GetDestinationsAsync(CancellationToken t = default) => db.Locations.AsNoTracking().Where(x => x.IsActive && x.IsPhysicallyPresent && !x.IsBlocked && x.OperationalRole != LocationOperationalRole.Wip).OrderBy(x => x.Code).ToListAsync(t);
    public async Task AddCatalogItemAsync(string kind, string code, string name, CancellationToken t = default) { code = code.Trim().ToUpperInvariant(); name = name.Trim(); if (kind == "shift") db.ProductionShifts.Add(new() { Code = code, Name = name }); else db.ProductionStages.Add(new() { Code = code, Name = name }); await db.SaveChangesAsync(t); }
}

public sealed record ProductionReceiptLink(Guid OrderId, string OrderNumber, Guid? BatchId, string? BatchNumber, decimal Quantity, decimal Received, decimal Pending, bool CanReceive);

public sealed record ProductionQueueSnapshot(string Signature, DateTimeOffset CheckedAt);
