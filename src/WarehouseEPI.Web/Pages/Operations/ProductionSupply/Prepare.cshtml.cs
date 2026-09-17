using Microsoft.AspNetCore.Mvc.Filters;
using WarehouseEPI.Web.Pages.Operations.Production;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Operations.ProductionSupply;

public sealed class PrepareModel(ProductionSupplyPreparationService preparations) : PageModel
{
    [BindProperty(SupportsGet=true)]public string? QueueSearch{get;set;}
    [BindProperty(SupportsGet=true)]public string? QueueCondition{get;set;}
    [BindProperty(SupportsGet=true)]public int QueuePage{get;set;}=1;
    public string? FailedHandler { get; private set; }
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (HttpMethods.IsPost(Request.Method)) {
            FailedHandler = Request.Query["handler"].ToString();
            if (!ProductionCapture.ValidateOnly(this, nameof(Input))) {
                context.Result = await HandleAsync(new(ProductionSupplyCommandStatus.ValidationFailed, Errors: ["Corrige las cantidades antes de confirmar."]), token: HttpContext.RequestAborted);
                return;
            }
        }
        await next();
    }
    public async Task<IActionResult> OnGetSaveResultAsync(Guid operationId, Guid lineId, CancellationToken token) {
        var result = await preparations.GetSaveResultAsync(operationId, lineId, token);
        return result is null ? NotFound() : new JsonResult(result);
    }
    public ProductionSupplyPreparationView? Detail { get; private set; }
    [BindProperty(SupportsGet = true)] public Guid LineId { get; set; }
    [BindProperty] public PreparationInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken token)
    {
        Detail = await preparations.GetAsync(LineId, token);
        if (Detail is null) return NotFound();
        Input = new PreparationInput
        {
            OperationId = Guid.NewGuid(), LineId = LineId, ExpectedRequestVersion = Detail.Line.RequestVersion,
            DestinationLocationId = Detail.DestinationLocationId, PreparationId = Detail.PreparationId,
            ExpectedPreparationVersion = Detail.PreparationVersion,
            Sources = Detail.Sources.Select(source => new SourceInput
            {
                Kind = source.Kind, LocationId = source.LocationId,
                Quantity = Detail.Selected.FirstOrDefault(x => x.Kind == source.Kind && x.LocationId == source.LocationId)?.Quantity ?? 0
            }).ToList()
        };
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken token) => await HandleAsync(await preparations.SaveAsync(
        new(Input.OperationId, Input.LineId, Input.ExpectedRequestVersion, Input.DestinationLocationId,
            Input.PreparationId, Input.ExpectedPreparationVersion, Selections(), Input.Pin), token), token: token);

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken token)
    {
        var result = await preparations.ConfirmAsync(new(Input.OperationId, Input.PreparationId ?? Guid.Empty,
            Input.ExpectedPreparationVersion, Input.ExpectedRequestVersion, Input.Pin, Selections()), token);
        if (result.Status == ProductionSupplyCommandStatus.Success)
        {
            var proof = await preparations.GetConfirmationResultAsync(Input.OperationId, token);
            if (proof is not null)
            {
                TempData["ConfirmationId"] = proof.ConfirmationId.ToString();
                TempData["ConfirmationOperationId"] = proof.OperationId.ToString();
                TempData["ConfirmedQuantity"] = proof.ConfirmedQuantity.ToString("0.####", CultureInfo.InvariantCulture);
                TempData["PendingQuantity"] = proof.PendingQuantity.ToString("0.####", CultureInfo.InvariantCulture);
                TempData["ConfirmationDestination"] = proof.Destination;
                TempData["ConfirmationResponsible"] = proof.Responsible;
                TempData["ConfirmationRecordedAt"] = proof.RecordedAt.ToString("dd/MM/yyyy HH:mm zzz", CultureInfo.InvariantCulture);
                TempData["ConfirmationMovements"] = proof.MovementCount.ToString(CultureInfo.InvariantCulture);
                TempData["ConfirmationAssignments"] = proof.WipAssignmentCount.ToString(CultureInfo.InvariantCulture);
            }
        }
        return await HandleAsync(result, "Surtimiento confirmado.", token);
    }

    public async Task<IActionResult> OnGetResultAsync(Guid operationId, CancellationToken token)
    {
        var result = await preparations.GetConfirmationResultAsync(operationId, token);
        return result is null ? NotFound() : new JsonResult(result);
    }

    public async Task<IActionResult> OnPostDiscardAsync(CancellationToken token) => await HandleAsync(await preparations.DiscardAsync(
        new(Input.OperationId, Input.PreparationId ?? Guid.Empty, Input.ExpectedPreparationVersion, Input.Pin, Input.Reason), token), "Preparación descartada.", token);

    public async Task<IActionResult> OnPostDestinationAsync(CancellationToken token) => await HandleAsync(await preparations.ChangeDestinationAsync(
        new(Input.OperationId, Input.LineId, Input.ExpectedRequestVersion, Input.DestinationLocationId, Input.Pin, Input.Reason), token), "Destino WIP actualizado.", token);

    public async Task<IActionResult> OnPostCancelAssignmentAsync(CancellationToken token) => await HandleAsync(await preparations.CancelWipAssignmentAsync(
        new(Input.OperationId, Input.LineId, Input.IssueLinkId, Input.ExpectedRequestVersion, Input.CancelQuantity, Input.Pin, Input.Reason), token),
        "Asignación WIP anulada; la cantidad volvió al pendiente.", token);

    private IReadOnlyList<ProductionSupplySourceSelection> Selections() => Input.Sources
        .Select(x => new ProductionSupplySourceSelection(x.Kind, x.LocationId, x.Quantity)).ToArray();

    private async Task<IActionResult> HandleAsync(ProductionSupplyCommandResult result, string success = "Preparación guardada.",
        CancellationToken token = default)
    {
        if (result.Status == ProductionSupplyCommandStatus.Success)
        {
            TempData["SuccessMessage"] = success;
            return RedirectToPage(new { lineId = Input.LineId, QueueSearch, QueueCondition, QueuePage });
        }
        var message = result.Status switch
        {
            ProductionSupplyCommandStatus.InvalidPin => "No fue posible validar el NIP o el usuario.",
            ProductionSupplyCommandStatus.ConcurrencyConflict => "La solicitud o la preparación cambió. Revisa las cantidades antes de continuar.",
            ProductionSupplyCommandStatus.IdempotencyConflict => "La operación ya se utilizó con otro contenido.",
            ProductionSupplyCommandStatus.NotFound => "La preparación ya no está disponible.",
            _ => string.Join(" ", result.ValidationErrors)
        };
        Input.Pin = string.Empty; ProductionCapture.ClearPins(this);
        var captured = Input.Sources.ToArray();
        Detail = await preparations.GetAsync(Input.LineId, token);
        if (Detail is null) return NotFound();
        Input.Sources = captured.ToList();
        foreach(var source in Detail.Sources.Where(source => !captured.Any(x=>x.Kind==source.Kind&&x.LocationId==source.LocationId)))
            Input.Sources.Add(new SourceInput{Kind=source.Kind,LocationId=source.LocationId});
        ModelState.AddModelError(string.Empty, message);
        return Page();
    }

    public sealed class PreparationInput
    {
        public Guid OperationId { get; set; }
        public Guid LineId { get; set; }
        public uint ExpectedRequestVersion { get; set; }
        public Guid DestinationLocationId { get; set; }
        public Guid? PreparationId { get; set; }
        public uint ExpectedPreparationVersion { get; set; }
        public List<SourceInput> Sources { get; set; } = [];
        public string Pin { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public Guid IssueLinkId { get; set; }
        [ProductionQuantity]public decimal CancelQuantity { get; set; }
    }

    public sealed class SourceInput
    {
        public ProductionSupplySourceKind Kind { get; set; }
        public Guid LocationId { get; set; }
        [ProductionQuantity]public decimal Quantity { get; set; }
    }
}
