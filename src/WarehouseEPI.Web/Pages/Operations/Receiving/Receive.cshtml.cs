using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations.Receiving;

public sealed class ReceiveModel(ReceivingQueryService query, ReceivingService service) : PageModel
{
    public ReceivingDocumentDetail Document { get; private set; } = null!;
    public IReadOnlyList<SharedLocationConflict> Conflicts { get; private set; } = [];
    [BindProperty] public InputModel Input { get; set; } = new();
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        if (!await LoadAsync(id, token)) return NotFound();
        if (!Document.CanReceive) return RedirectToPage("Details", new { id });
        Input = new() { OperationId = Guid.NewGuid(), Lines = Document.Lines.Select(item => new LineInput { ProductId = item.ProductId, ProductLabel = $"{item.Sku} · pendiente {Math.Max(0,item.Expected-item.Received):0.####} {item.Unit}" }).ToList() };
        return Page();
    }
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken token)
    {
        if (!await LoadAsync(id, token)) return NotFound();
        if (!ModelState.IsValid) { Input.Pin = string.Empty; EnsureLine(); return Page(); }
        var result = await service.ConfirmAsync(new(Input.OperationId, id, Input.Pin,
            Input.Lines.Select(item => new ConfirmReceivingLineCommand(item.ProductId, item.Quantity, item.DestinationLocationId, item.ExternalLotReference)).ToArray(),
            Input.DifferenceAcknowledged, Input.DifferenceNotes,
            Input.ApprovedSharedLocationIds.Select(locationId => Input.Lines.Where(line => line.DestinationLocationId == locationId).Select(line => new SharedAssignmentApproval(line.ProductId, locationId))).SelectMany(item => item).ToArray()), token);
        Input.Pin = string.Empty;
        if (result.Status == ReceivingCommandStatus.Success && result.MovementId is Guid movementId) { TempData["Success"] = "Recepción confirmada y Entrada registrada."; return RedirectToPage("/Operations/Receipt", new { id = movementId }); }
        if (result.Status == ReceivingCommandStatus.RequiresLocationSharingConfirmation) Conflicts = result.Conflicts;
        ModelState.AddModelError(string.Empty, Message(result)); EnsureLine(); return Page();
    }
    private async Task<bool> LoadAsync(Guid id, CancellationToken token) { var value=await query.GetAsync(id,token); if(value is null)return false; Document=value; return true; }
    private void EnsureLine(){if(Input.Lines.Count==0)Input.Lines.Add(new());}
    private static string Message(ReceivingCommandResult result)=>result.Status switch{ReceivingCommandStatus.InvalidPin=>"NIP inválido o usuario sin permiso operativo.",ReceivingCommandStatus.RequiresDifferenceAcknowledgement=>result.ValidationErrors.FirstOrDefault()??"Reconoce las diferencias.",ReceivingCommandStatus.RequiresLocationSharingConfirmation=>"Confirma expresamente las ubicaciones compartidas y vuelve a introducir el NIP.",ReceivingCommandStatus.ConcurrencyConflict=>"El documento cambió mientras confirmabas; recárgalo.",ReceivingCommandStatus.IdempotencyConflict=>"La operación ya fue usada con datos distintos.",_=>result.ValidationErrors.FirstOrDefault()??"No fue posible confirmar la recepción."};
    public sealed class InputModel { public Guid OperationId{get;set;}=Guid.NewGuid(); public List<LineInput> Lines{get;set;}=[]; public bool DifferenceAcknowledged{get;set;} [StringLength(500)] public string? DifferenceNotes{get;set;} [Required,RegularExpression("^[0-9]{4,8}$")] public string Pin{get;set;}=string.Empty; public List<Guid> ApprovedSharedLocationIds{get;set;}=[]; }
    public sealed class LineInput { public Guid ProductId{get;set;} public string? ProductLabel{get;set;} public decimal Quantity{get;set;} public Guid DestinationLocationId{get;set;} [StringLength(120)] public string? ExternalLotReference{get;set;} }
}
