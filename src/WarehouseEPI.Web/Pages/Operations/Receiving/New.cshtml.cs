using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations.Receiving;

public sealed class NewModel(ReceivingService service) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public void OnGet() => Input = new() { OperationId = Guid.NewGuid(), Lines = [new()] };
    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        if (!ModelState.IsValid) { Input.Pin = string.Empty; EnsureLine(); return Page(); }
        var result = await service.OpenAsync(new(Input.OperationId, Input.Type, Input.Number, Input.Origin, Input.DocumentDate, Input.Notes, Input.Pin,
            Input.Lines.Select(item => new OpenReceivingDocumentLineCommand(item.ProductId, item.ExpectedQuantity)).ToArray()), token);
        Input.Pin = string.Empty;
        if (result.Status == ReceivingCommandStatus.Success && result.DocumentId is Guid id) { TempData["Success"] = "Documento abierto; las cantidades esperadas quedaron congeladas."; return RedirectToPage("Details", new { id }); }
        ModelState.AddModelError(string.Empty, Message(result)); EnsureLine(); return Page();
    }
    private void EnsureLine() { if (Input.Lines.Count == 0) Input.Lines.Add(new()); }
    private static string Message(ReceivingCommandResult result) => result.Status switch { ReceivingCommandStatus.InvalidPin => "NIP inválido o usuario sin permiso operativo.", ReceivingCommandStatus.IdempotencyConflict => "La operación ya fue usada con contenido diferente.", _ => result.ValidationErrors.FirstOrDefault() ?? "No fue posible abrir el documento." };
    public sealed class InputModel
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public ReceivingDocumentType Type { get; set; }
        [Required, StringLength(120)] public string Number { get; set; } = string.Empty;
        [Required, StringLength(160)] public string Origin { get; set; } = string.Empty;
        public DateOnly? DocumentDate { get; set; }
        [StringLength(500)] public string? Notes { get; set; }
        [Required, RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = string.Empty;
        public List<LineInput> Lines { get; set; } = [];
    }
    public sealed class LineInput { public Guid ProductId { get; set; } public decimal ExpectedQuantity { get; set; } }
}
