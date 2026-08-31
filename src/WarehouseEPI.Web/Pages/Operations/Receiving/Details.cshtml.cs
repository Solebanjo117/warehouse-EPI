using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations.Receiving;

public sealed class DetailsModel(ReceivingQueryService query, ReceivingService service, WarehouseClock clock) : PageModel
{
    public ReceivingDocumentDetail Document { get; private set; } = null!;
    public Dictionary<Guid, DateTimeOffset> ConfirmationDates { get; } = [];
    public List<(ReceivingDocumentEventDetail Event, DateTimeOffset Local)> LocalEvents { get; } = [];
    public DateTimeOffset LocalOpenedAt { get; private set; }
    [BindProperty] public ActionInput Input { get; set; } = new();
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token) => await LoadAsync(id, token) ? Page() : NotFound();
    public async Task<IActionResult> OnPostCloseAsync(Guid id, CancellationToken token) => await CompleteAsync(id, false, token);
    public async Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken token) => await CompleteAsync(id, true, token);
    private async Task<IActionResult> CompleteAsync(Guid id, bool cancel, CancellationToken token)
    {
        if (!ModelState.IsValid) { Input.Pin = string.Empty; await LoadAsync(id, token); return Page(); }
        var command = new CompleteReceivingDocumentCommand(Input.OperationId, id, Input.Pin, Input.Reason);
        var result = cancel ? await service.CancelAsync(command, token) : await service.CloseAsync(command, token);
        Input.Pin = string.Empty;
        if (result.Status == ReceivingCommandStatus.Success) { TempData["Success"] = cancel ? "Documento cancelado." : "Documento cerrado con diferencias."; return RedirectToPage(new { id }); }
        ModelState.AddModelError(string.Empty, result.Status == ReceivingCommandStatus.InvalidPin ? "NIP inválido o usuario sin permiso operativo." : result.ValidationErrors.FirstOrDefault() ?? "No fue posible actualizar el documento.");
        await LoadAsync(id, token); return Page();
    }
    private async Task<bool> LoadAsync(Guid id, CancellationToken token)
    {
        var item = await query.GetAsync(id, token); if (item is null) return false; Document = item;
        LocalOpenedAt = await clock.ConvertAsync(item.OpenedAt, token);
        foreach (var confirmation in item.Confirmations) ConfirmationDates[confirmation.Id] = await clock.ConvertAsync(confirmation.OccurredAt, token);
        foreach (var auditEvent in item.Events) LocalEvents.Add((auditEvent, await clock.ConvertAsync(auditEvent.RecordedAt, token)));
        if (Input.OperationId == Guid.Empty) Input.OperationId = Guid.NewGuid(); return true;
    }
    public sealed class ActionInput { public Guid OperationId { get; set; } = Guid.NewGuid(); [Required, StringLength(500)] public string Reason { get; set; } = string.Empty; [Required, RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = string.Empty; }
}
