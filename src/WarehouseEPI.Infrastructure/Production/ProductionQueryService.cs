using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public sealed class ProductionQueryService(WarehouseDbContext db)
{
    public async Task<IReadOnlyList<ProductionOrderRow>> GetOrdersAsync(string? search=null,ProductionWorkOrderStatus? status=null,int take=100,CancellationToken token=default)
    {
        var q=db.ProductionWorkOrders.AsNoTracking().Include(x=>x.Product).ThenInclude(x=>x.BaseUnit).Include(x=>x.Events).AsQueryable();
        search=string.IsNullOrWhiteSpace(search)?null:search.Trim();
        if(search is not null)q=q.Where(x=>x.Number.Contains(search)||(x.ExternalReference!=null&&x.ExternalReference.Contains(search))||x.Product.Sku.Contains(search)||x.Batches.Any(b=>b.Number.Contains(search)||b.FinishedProductLot.Number.Contains(search)));
        if(status.HasValue)q=q.Where(x=>x.Status==status);
        return await Project(q.OrderByDescending(x=>x.CreatedAt).Take(Math.Clamp(take,1,200))).ToListAsync(token);
    }

    public async Task<ProductionOrderPage> SearchOrdersAsync(ProductionOrderSearch search, CancellationToken token=default)
    {
        var q=db.ProductionWorkOrders.AsNoTracking().AsQueryable();
        var term=string.IsNullOrWhiteSpace(search.Search)?null:search.Search.Trim();
        if(term is not null)q=q.Where(x=>x.Number.Contains(term)||(x.ExternalReference!=null&&x.ExternalReference.Contains(term))||
            x.Product.Sku.Contains(term)||x.Batches.Any(b=>b.Number.Contains(term)||b.FinishedProductLot.Number.Contains(term)));
        if(search.Status.HasValue)q=q.Where(x=>x.Status==search.Status);
        if(search.ActiveStageId.HasValue)q=q.Where(x=>
            x.Batches.Any(b=>b.Results.OrderByDescending(r=>r.RecordedAt).Select(r=>r.WorkOrderStage.SourceStageId).FirstOrDefault()==search.ActiveStageId)||
            (x.Stages.Any(s=>s.Sequence==1&&s.SourceStageId==search.ActiveStageId)&&x.Batches.Any(b=>b.Results.Count==0))||
            x.Events.Any(e=>e.Type==ProductionEventType.Received&&e.RelatedStage!.SourceStageId==search.ActiveStageId));
        if(search.From.HasValue)q=q.Where(x=>x.CreatedAt>=search.From.Value.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
        if(search.To.HasValue)q=q.Where(x=>x.CreatedAt<search.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
        if(search.AlertsOnly)q=q.Where(x=>x.Events.Any(e=>e.Type==ProductionEventType.Delivered&&e.Quantity>
            x.Events.Where(r=>r.RelatedEventId==e.Id&&(r.Type==ProductionEventType.Received||r.Type==ProductionEventType.DifferenceReturned||r.Type==ProductionEventType.DifferenceLost)).Sum(r=>r.Quantity))||
            (x.Status==ProductionWorkOrderStatus.Closed&&x.Events.Where(e=>e.Type==ProductionEventType.WarehouseReceived).Sum(e=>e.Quantity)!=x.TargetQuantity));
        var page=Math.Max(1,search.Page);var size=Math.Clamp(search.PageSize,1,100);var count=await q.CountAsync(token);
        var items=await Project(q.OrderByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Number).Skip((page-1)*size).Take(size)).ToListAsync(token);
        return new(items,page,size,count);
    }

    private static IQueryable<ProductionOrderRow> Project(IQueryable<ProductionWorkOrder> q)=>q.Select(x=>new ProductionOrderRow(
        x.Id,x.Number,x.ExternalReference,x.Product.Sku,x.Product.Description,x.TargetQuantity,
        x.Events.Where(e=>e.Type==ProductionEventType.WarehouseReceived).Sum(e=>e.Quantity),x.Product.BaseUnit.Code,x.Status,x.DueDate,
        x.Batches.SelectMany(b=>b.Results).OrderByDescending(r=>r.RecordedAt).Select(r=>r.WorkOrderStage.Name).FirstOrDefault(),
        x.Events.Count(e=>e.Type==ProductionEventType.Delivered&&e.Quantity>x.Events.Where(r=>r.RelatedEventId==e.Id&&(r.Type==ProductionEventType.Received||r.Type==ProductionEventType.DifferenceReturned||r.Type==ProductionEventType.DifferenceLost)).Sum(r=>r.Quantity)),
        x.UsesBatchTraceability));
    public async Task<ProductionOrderDetail?> GetAsync(Guid id,CancellationToken token=default)
    {
        var o=await db.ProductionWorkOrders.AsNoTracking().Include(x=>x.Product).ThenInclude(x=>x.BaseUnit).Include(x=>x.Unit).Include(x=>x.Stages).Include(x=>x.Events).ThenInclude(x=>x.ResponsibleUser).Include(x=>x.Events).ThenInclude(x=>x.Shift).SingleOrDefaultAsync(x=>x.Id==id,token);
        if(o is null)return null;var names=o.Stages.ToDictionary(x=>x.Id,x=>x.Name);var progress=ProductionService.BuildProgress(o);
        var events=o.Events.OrderByDescending(x=>x.RecordedAt).Select(x=>new ProductionEventItem(x.Type,x.WorkOrderStageId is Guid s&&names.TryGetValue(s,out var sn)?sn:null,x.RelatedStageId is Guid r&&names.TryGetValue(r,out var rn)?rn:null,x.Quantity,x.GoodQuantity,x.ReworkQuantity,x.ScrapQuantity,x.ResponsibleUser.FullName,x.Shift?.Name,x.Reason,x.RecordedAt,x.InventoryMovementId)).ToArray();
        return new(o.Id,o.Number,o.ExternalReference,o.Product.Sku,o.Product.Description,o.Unit.Code,o.Unit.AllowsDecimals,o.TargetQuantity,o.AuthorizedQuantity,o.DueDate,o.Notes,o.Status,o.Version,o.Events.Where(x=>x.Type==ProductionEventType.WarehouseReceived).Sum(x=>x.Quantity),progress,events,o.UsesBatchTraceability,o.RecipeVersion);
    }
    public Task<List<ProductionStage>> GetStagesAsync(CancellationToken t=default)=>db.ProductionStages.AsNoTracking().OrderBy(x=>x.Name).ToListAsync(t);
    public Task<List<ProductionShift>> GetShiftsAsync(CancellationToken t=default)=>db.ProductionShifts.AsNoTracking().OrderBy(x=>x.Name).ToListAsync(t);
    public Task<List<Product>> GetProductsWithoutRouteAsync(CancellationToken t=default)=>db.Products.AsNoTracking().Where(x=>x.IsActive&&!db.ProductionRoutes.Any(r=>r.ProductId==x.Id&&r.IsActive)).OrderBy(x=>x.Sku).Take(2000).ToListAsync(t);
    public Task<List<Product>> GetRoutedProductsAsync(CancellationToken t=default)=>db.Products.AsNoTracking().Where(x=>x.IsActive&&db.ProductionRoutes.Any(r=>r.ProductId==x.Id&&r.IsActive)).OrderBy(x=>x.Sku).Take(2000).ToListAsync(t);
    public Task<List<Location>> GetDestinationsAsync(CancellationToken t=default)=>db.Locations.AsNoTracking().Where(x=>x.IsActive&&x.IsPhysicallyPresent&&!x.IsBlocked&&x.OperationalRole!=LocationOperationalRole.Wip).OrderBy(x=>x.Code).ToListAsync(t);
    public async Task AddCatalogItemAsync(string kind,string code,string name,CancellationToken t=default){code=code.Trim().ToUpperInvariant();name=name.Trim();if(kind=="shift")db.ProductionShifts.Add(new(){Code=code,Name=name});else db.ProductionStages.Add(new(){Code=code,Name=name});await db.SaveChangesAsync(t);}
}
