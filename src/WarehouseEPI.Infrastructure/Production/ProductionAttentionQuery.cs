using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionAttentionRow
{
    public Guid Id { get; init; }
    public int SupplyProblems { get; init; }
    public int PendingLines { get; init; }
    public int ReworkCases { get; init; }
    public int LateRework { get; init; }
    public int Overdue { get; init; }
    public int Inactive { get; init; }
    public int Count => SupplyProblems + PendingLines + ReworkCases + Overdue + Inactive;
    public string Description => string.Join(" · ", new[] {
        SupplyProblems>0?$"{SupplyProblems} problemas de surtimiento":null,
        PendingLines>0?$"{PendingLines} materiales pendientes":null,
        ReworkCases>0?$"{ReworkCases} retrabajos pendientes ({LateRework} con atraso)":null,
        Overdue>0?"Fecha requerida vencida":null,Inactive>0?"Inactividad supera el umbral":null }.Where(x => x is not null));
}

public static class ProductionAttentionQuery
{
    public static IQueryable<ProductionAttentionRow> Query(WarehouseDbContext db, IQueryable<ProductionWorkOrder> orders, DateTimeOffset now, DateOnly today) =>
        orders.Select(x => new ProductionAttentionRow
        {
            Id = x.Id,
            SupplyProblems = x.SupplyRequests.SelectMany(r => r.Events).Count(e => e.Type == ProductionSupplyEventType.ProblemReported),
            PendingLines = x.SupplyRequests.SelectMany(r => r.Lines).Count(l => l.RequiredQuantity + l.ReopenedQuantity - l.CancelledQuantity > l.IssueLinks.Sum(i => i.Quantity - i.CancelledQuantity)),
            ReworkCases = db.ProductionReworkCases.Count(c => c.WorkOrderId == x.Id
                && !x.Events.Any(e => e.Type == ProductionEventType.ResultReversed && e.RelatedEvent!.OperationId == c.OriginResult.OperationId)
                && c.OriginResult.ReworkQuantity > c.Attempts.Where(a => !x.Events.Any(e => e.Type == ProductionEventType.ResultReversed && e.RelatedEvent!.OperationId == a.Result.OperationId)).Sum(a => a.Result.GoodQuantity + a.Result.ScrapQuantity)),
            LateRework = db.ProductionReworkCases.Count(c => c.WorkOrderId == x.Id && c.WorkOrderStage.SourceStage.ReworkAlertHours != null && c.OriginAt <= now.AddHours(-c.WorkOrderStage.SourceStage.ReworkAlertHours!.Value)
                && !x.Events.Any(e => e.Type == ProductionEventType.ResultReversed && e.RelatedEvent!.OperationId == c.OriginResult.OperationId)
                && c.OriginResult.ReworkQuantity > c.Attempts.Where(a => !x.Events.Any(e => e.Type == ProductionEventType.ResultReversed && e.RelatedEvent!.OperationId == a.Result.OperationId)).Sum(a => a.Result.GoodQuantity + a.Result.ScrapQuantity)),
            Overdue = x.DueDate < today && x.Status != ProductionWorkOrderStatus.Draft && x.Status != ProductionWorkOrderStatus.Cancelled && x.Events.Where(e => e.Type == ProductionEventType.WarehouseReceived).Sum(e => e.Quantity) < x.TargetQuantity ? 1 : 0,
            Inactive = (x.Status == ProductionWorkOrderStatus.Released || x.Status == ProductionWorkOrderStatus.InProgress || x.Status == ProductionWorkOrderStatus.PrincipalClosed) && x.Stages.Any(s => s.SourceStage.InactivityAlertHours != null
                && (x.Events.Where(e => (e.WorkOrderStageId == s.Id || e.RelatedStageId == s.Id) && e.Type != ProductionEventType.ResultReversed && !x.Events.Any(r => r.Type == ProductionEventType.ResultReversed && r.RelatedEventId == e.Id)).Max(e => (DateTimeOffset?)e.RecordedAt) ?? x.ReleasedAt ?? x.CreatedAt) <= now.AddHours(-s.SourceStage.InactivityAlertHours!.Value)
                && ((s.Sequence == 1 ? x.AuthorizedQuantity : x.Events.Where(e => e.RelatedStageId == s.Id && e.Type == ProductionEventType.Received).Sum(e => e.Quantity)) > x.Events.Where(e => e.WorkOrderStageId == s.Id && e.Type == ProductionEventType.Processed && !x.Events.Any(r => r.Type == ProductionEventType.ResultReversed && r.RelatedEventId == e.Id)).Sum(e => e.Quantity)
                    || x.Events.Where(e => e.WorkOrderStageId == s.Id && (e.Type == ProductionEventType.Processed || e.Type == ProductionEventType.Reworked) && !x.Events.Any(r => r.Type == ProductionEventType.ResultReversed && r.RelatedEventId == e.Id)).Sum(e => e.GoodQuantity) + x.Events.Where(e => e.WorkOrderStageId == s.Id && e.Type == ProductionEventType.DifferenceReturned).Sum(e => e.Quantity) > x.Events.Where(e => e.WorkOrderStageId == s.Id && (e.Type == ProductionEventType.Delivered || e.Type == ProductionEventType.WarehouseReceived)).Sum(e => e.Quantity))) ? 1 : 0
        });

    public static IQueryable<ProductionWorkOrder> WithPendingStage(IQueryable<ProductionWorkOrder> orders, Guid sourceStageId) => orders.Where(x => x.Stages.Any(s => s.SourceStageId == sourceStageId && (
        ((s.Sequence != 1 || x.Status != ProductionWorkOrderStatus.PrincipalClosed) && (s.Sequence == 1 ? x.AuthorizedQuantity : x.Events.Where(e => e.RelatedStageId == s.Id && e.Type == ProductionEventType.Received).Sum(e => e.Quantity)) > x.Events.Where(e => e.WorkOrderStageId == s.Id && e.Type == ProductionEventType.Processed && !x.Events.Any(r => r.Type == ProductionEventType.ResultReversed && r.RelatedEventId == e.Id)).Sum(e => e.Quantity))
        || x.Events.Where(e => e.WorkOrderStageId == s.Id && (e.Type == ProductionEventType.Processed || e.Type == ProductionEventType.Reworked) && !x.Events.Any(r => r.Type == ProductionEventType.ResultReversed && r.RelatedEventId == e.Id)).Sum(e => e.GoodQuantity) + x.Events.Where(e => e.WorkOrderStageId == s.Id && e.Type == ProductionEventType.DifferenceReturned).Sum(e => e.Quantity) > x.Events.Where(e => e.WorkOrderStageId == s.Id && (e.Type == ProductionEventType.Delivered || e.Type == ProductionEventType.WarehouseReceived)).Sum(e => e.Quantity)
        || x.Events.Where(e => e.WorkOrderStageId == s.Id && (e.Type == ProductionEventType.Processed || e.Type == ProductionEventType.Reworked) && !x.Events.Any(r => r.Type == ProductionEventType.ResultReversed && r.RelatedEventId == e.Id)).Sum(e => e.ReworkQuantity) > x.Events.Where(e => e.WorkOrderStageId == s.Id && e.Type == ProductionEventType.Reworked && !x.Events.Any(r => r.Type == ProductionEventType.ResultReversed && r.RelatedEventId == e.Id)).Sum(e => e.Quantity)
        || x.Events.Any(e => e.Type == ProductionEventType.Delivered && (e.WorkOrderStageId == s.Id || e.RelatedStageId == s.Id) && e.Quantity > x.Events.Where(r => r.RelatedEventId == e.Id && (r.Type == ProductionEventType.Received || r.Type == ProductionEventType.DifferenceReturned || r.Type == ProductionEventType.DifferenceLost)).Sum(r => r.Quantity)))));
}
