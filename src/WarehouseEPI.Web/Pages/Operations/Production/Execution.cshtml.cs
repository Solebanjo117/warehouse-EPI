using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations.Production;

public sealed class ExecutionModel(WarehouseDbContext db, ProductionExecutionService execution, ProductionMaterialService materials, ProductionQueryService query, WarehouseClock clock, TimeProvider timeProvider) : PageModel
{
    public ProductionWorkOrder Order { get; private set; } = null!;
    public IReadOnlyList<ReworkView> Rework { get; private set; } = [];
    public IReadOnlyList<ProductionReason> Reasons { get; private set; } = [];
    public IReadOnlyList<ProductionMaterialIssueRow> Issues { get; private set; } = [];
    public IReadOnlyList<ProductionExecutionAudit> History { get; private set; } = [];
    public IReadOnlyList<ProductionReworkRetention> CurrentRetentions { get; private set; } = [];
    public IReadOnlyList<AuditView> AuditRows { get; private set; } = [];
    public Dictionary<Guid, string> ReworkDates { get; } = [];
    public Dictionary<Guid, int> ReworkAge { get; } = [];
    public List<ProductionBlockingTask> ClosureWarnings { get; } = [];
    [BindProperty(SupportsGet=true)]public string? SelectedAction{get;set;}
    [BindProperty] public InputModel Input { get; set; } = new();
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        if (!await LoadAsync(id, token)) return NotFound();
        Input.Action=SelectedAction is not null&&ProductionActionPolicy.Allows(Order.Status,SelectedAction)?SelectedAction:ProductionActionPolicy.ExecutionActions(Order.Status).FirstOrDefault()?.Code??"";
        Input.Version = Order.Version; Input.Target = Order.TargetQuantity; Input.Authorized = Order.AuthorizedQuantity; Input.DueDate = Order.DueDate;
        Input.Materials = Order.MaterialPlan.Select(x => new MaterialInput { PlanId = x.Id, Quantity = x.PlannedQuantity }).ToList();
        Input.Retentions = Issues.Where(x=>x.Pending>0).Select(x=> {
            var retained=CurrentRetentions.SingleOrDefault(r=>r.IssueLinkId==x.IssueLinkId);
            return new RetentionInput{IssueLinkId=x.IssueLinkId,Selected=retained is not null,CaseId=retained?.ReworkCaseId??Guid.Empty,Quantity=retained?.Quantity??0};
        }).Concat(Order.SupplyRequests.SelectMany(x=>x.Lines).SelectMany(x=>x.Reservations).Where(x=>x.Quantity>x.ReleasedQuantity).Select(x=>{
            var retained=CurrentRetentions.SingleOrDefault(r=>r.WarehouseReservationId==x.Id);
            return new RetentionInput{ReservationId=x.Id,Selected=retained is not null,CaseId=retained?.ReworkCaseId??Guid.Empty,Quantity=retained?.Quantity??0};
        })).ToList();
        return Page();
    }
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken token)
    {
        if (ProductionCapture.ValidateOnly(this, nameof(Input)))
        {
            var result = await execution.ApplyAsync(new(Input.OperationId, id, Input.Version, Input.Action, Input.Pin,
                Input.ReasonId, Input.Comment, Input.AdminPin, Input.Target, Input.Authorized, Input.DueDate,
                Input.Materials.Select(x => new ExecutionMaterial(x.PlanId, x.Quantity)).ToArray(),
                Input.Retentions.Where(x => x.Selected).Select(x => new ExecutionRetention(x.CaseId, x.IssueLinkId, x.ReservationId, x.Quantity)).ToArray(),
                Input.CaseId, Input.PlanId, Input.Quantity), token);
            if (result.Status == ProductionCommandStatus.Success) { TempData["Success"] = "Operación confirmada y registrada en el historial."; return RedirectToPage(new { id }); }
            ModelState.AddModelError("", result.ValidationErrors.FirstOrDefault() ?? (result.Status == ProductionCommandStatus.InvalidPin ? "NIP inválido o sin permiso para esta acción." : "La orden cambió o la operación ya se utilizó. Consulta el historial y recarga antes de repetir."));
        }
        Input.Pin = Input.AdminPin = ""; ModelState.Remove("Input.Pin"); ModelState.Remove("Input.AdminPin");
        if (!await LoadAsync(id, token)) return NotFound();
        return Page();
    }
    private async Task<bool> LoadAsync(Guid id, CancellationToken token)
    {
        var order = await db.ProductionWorkOrders.AsNoTracking().Include(x => x.Product).Include(x => x.Unit)
            .Include(x => x.Stages).Include(x => x.Events).Include(x => x.Batches)
            .Include(x => x.MaterialPlan).ThenInclude(x => x.MaterialProduct)
            .Include(x => x.MaterialPlan).ThenInclude(x => x.Unit)
            .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.Reservations).ThenInclude(x => x.Location)
            .Include(x => x.SupplyRequests).ThenInclude(x => x.Lines).ThenInclude(x => x.IssueLinks)
            .SingleOrDefaultAsync(x => x.Id == id, token);
        if (order is null) return false;
        Order = order; Rework = await execution.GetReworkAsync(id, token);
        Reasons = await db.ProductionReasons.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Category).ThenBy(x => x.Description).ToListAsync(token);
        Issues = await materials.GetIssuesAsync(id, token);
        var detail = await query.GetAsync(id, token);
        ClosureWarnings.AddRange(ProductionActionPolicy.ClosureProgress(detail!.Stages));
        var retained = await db.ProductionReworkRetentions.AsNoTracking().Where(x => x.ReworkCase.WorkOrderId == id).ToListAsync(token);
        CurrentRetentions = retained;
        foreach(var reservation in order.SupplyRequests.SelectMany(x=>x.Lines).SelectMany(x=>x.Reservations).Where(x=>x.Quantity>x.ReleasedQuantity&&!retained.Any(r=>r.WarehouseReservationId==x.Id&&r.Quantity>=x.Quantity-x.ReleasedQuantity)))
            ClosureWarnings.Add(new($"Reserva en {reservation.Location.Code}: revisa qué conservar y qué liberar.","retain",Url:$"/Operations/Production/Execution?id={id}&SelectedAction=retain"));
        if(order.Status==ProductionWorkOrderStatus.PrincipalClosed)foreach(var item in Rework.Where(x=>x.Pending>0))
            ClosureWarnings.Add(new($"{item.Stage}: {item.Pending:0.####} de retrabajo antes del cierre definitivo.","rework",Url:$"/Operations/Production/Work?id={id}&BatchId={item.BatchId}&ReworkCaseId={item.Id}"));
        if(HttpMethods.IsPost(Request.Method)&&Input.Action=="retain") {
            foreach(var issue in Issues.Where(x=>x.Pending>0&&!Input.Retentions.Any(r=>r.IssueLinkId==x.IssueLinkId)))
                Input.Retentions.Add(new RetentionInput{IssueLinkId=issue.IssueLinkId});
            foreach(var reservation in order.SupplyRequests.SelectMany(x=>x.Lines).SelectMany(x=>x.Reservations).Where(x=>x.Quantity>x.ReleasedQuantity&&!Input.Retentions.Any(r=>r.ReservationId==x.Id)))
                Input.Retentions.Add(new RetentionInput{ReservationId=reservation.Id});
        }
        foreach (var issue in Issues.Where(x => x.Pending > 0 && !retained.Any(r => r.IssueLinkId == x.IssueLinkId && r.Quantity >= x.Pending)))
            ClosureWarnings.Add(new($"{issue.ProductSku}: conciliar {issue.Pending:0.####} {issue.Unit} en {issue.WipCode}; devolver o retener para retrabajo.",$"material-{issue.StageId}",issue.StageId));
        foreach (var line in order.SupplyRequests.SelectMany(x => x.Lines).Where(x => x.ReworkCaseId is null && ProductionExecutionService.Pending(x) > 0))
            ClosureWarnings.Add(new($"{order.MaterialPlan.Single(x => x.Id == line.MaterialPlanId).MaterialProduct.Sku}: surtimiento ordinario pendiente de conciliar.","supply",Url:$"/Operations/ProductionSupply/Index?Search={Uri.EscapeDataString(order.Number)}"));
        History = await db.ProductionExecutionAudits.AsNoTracking().Where(x => x.WorkOrderId == id).OrderByDescending(x => x.RecordedAt).Take(50).ToListAsync(token);
        foreach (var item in Rework)
        {
            ReworkDates[item.Id] = (await clock.ConvertAsync(item.OriginAt, token)).ToString("dd/MM/yyyy HH:mm");
            ReworkAge[item.Id] = Math.Max(0, (int)(timeProvider.GetUtcNow() - item.OriginAt).TotalDays);
        }
        var userIds = History.SelectMany(x => new[] { x.ResponsibleUserId, x.AuthorizedByUserId ?? x.ResponsibleUserId }).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName, token);
        var auditRows = new List<AuditView>();
        foreach (var item in History)
            auditRows.Add(new(item.OperationId, (await clock.ConvertAsync(item.RecordedAt, token)).ToString("dd/MM/yyyy HH:mm"),
                ActionLabel(item.Action), item.Reason, users.GetValueOrDefault(item.ResponsibleUserId, "Usuario histórico"),
                item.AuthorizedByUserId is Guid admin ? users.GetValueOrDefault(admin, "ADMIN") : null,
                Describe(item.BeforeJson), Describe(item.AfterJson)));
        AuditRows = auditRows;
        if (Input.Action != "adjust" && Input.Materials.Count == 0)
            Input.Materials = Order.MaterialPlan.Select(x => new MaterialInput { PlanId = x.Id, Quantity = x.PlannedQuantity }).ToList();
        return true;
    }
    public sealed record AuditView(Guid OperationId, string Date, string Action, string Reason, string User,
        string? Administrator, IReadOnlyList<string> Before, IReadOnlyList<string> After);
    private static string ActionLabel(string action) => action switch
    { "principal" => "Cierre principal", "definitive" => "Cierre definitivo", "adjust" => "Ajuste de orden", "retain" => "Selección de reservas", "request" => "Solicitud para retrabajo", "reopen" => "Reapertura", "rework" => "Intento de retrabajo", "result" => "Resultado de proceso", _ => "Actualización" };
    private IReadOnlyList<string> Describe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("State", out var state)) return Describe(state.GetString()!);
        var items = new List<string>();
        foreach (var (key, label) in new[] { ("OriginalTargetQuantity", "Meta original"), ("TargetQuantity", "Meta vigente"), ("AuthorizedQuantity", "Autorizado"), ("Received", "Recibido"), ("Difference", "Diferencia"), ("InputQuantity", "Procesado"), ("GoodQuantity", "Bueno"), ("ReworkQuantity", "Retrabajo"), ("ScrapQuantity", "Merma") })
            if (root.TryGetProperty(key, out var value) && value.TryGetDecimal(out var quantity)) items.Add($"{label}: {quantity:0.####} {Order.Unit.Code}");
        if (root.TryGetProperty("DueDate", out var due) && due.ValueKind == JsonValueKind.String) items.Add($"Fecha requerida: {due.GetString()}");
        if (root.TryGetProperty("Materials", out var materialRows))
            foreach (var material in materialRows.EnumerateArray())
                if (material.TryGetProperty("Id", out var id) && Order.MaterialPlan.SingleOrDefault(x => x.Id == id.GetGuid()) is { } plan)
                    items.Add($"{plan.MaterialProduct.Sku}: {material.GetProperty("PlannedQuantity").GetDecimal():0.####} {plan.Unit.Code}");
        return items;
    }
    public sealed class InputModel
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public uint Version { get; set; }
        public string Action { get; set; } = "principal";
        [Required] public string Pin { get; set; } = "";
        public string? AdminPin { get; set; }
        public Guid ReasonId { get; set; }
        [StringLength(300)] public string? Comment { get; set; }
        [ProductionQuantity]public decimal Target { get; set; }
        [ProductionQuantity]public decimal Authorized { get; set; }
        public DateOnly? DueDate { get; set; }
        public List<MaterialInput> Materials { get; set; } = [];
        public List<RetentionInput> Retentions { get; set; } = [];
        public Guid? CaseId { get; set; }
        public Guid? PlanId { get; set; }
        [ProductionQuantity]public decimal Quantity { get; set; }
    }
    public sealed class MaterialInput { public Guid PlanId { get; set; } [ProductionQuantity]public decimal Quantity { get; set; } }
    public sealed class RetentionInput
    {
        public bool Selected { get; set; }
        public Guid CaseId { get; set; }
        public Guid? IssueLinkId { get; set; }
        public Guid? ReservationId { get; set; }
        [ProductionQuantity]public decimal Quantity { get; set; }
    }
}
