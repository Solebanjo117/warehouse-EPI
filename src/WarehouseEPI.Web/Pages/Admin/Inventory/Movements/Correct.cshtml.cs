using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Movements;

[Authorize(Policy = "AdminOnly")]
public sealed class CorrectModel(WarehouseDbContext db, InventoryCorrectionService corrections, InventoryQueryService inventory) : PageModel
{
    public InventoryMovementDetail Movement { get; private set; } = null!;
    public bool CanReplace { get; private set; }
    [BindProperty] public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        if (!await LoadAsync(id, token)) return NotFound();
        var line = await db.InventoryMovementLines.AsNoTracking().Include(l => l.BalanceChanges).SingleAsync(l => l.MovementId == id, token);
        Input = InputModel.From(line, Movement.Type); return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken token)
    {
        if (!await LoadAsync(id, token)) return NotFound();
        if (!ModelState.IsValid) { Input.Pin = string.Empty; return Page(); }
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var requestedBy)) return Forbid();
        InventoryReplacementCommand? replacement = null;
        if (Input.CreateReplacement)
        {
            if (!CanReplace) { ModelState.AddModelError(string.Empty, "Esta interfaz solo puede reemplazar movimientos de una línea."); Input.Pin = string.Empty; return Page(); }
            if (Input.Type == InventoryMovementType.Adjustment && Input.LocationId is Guid locationId)
                Input.ExpectedBalanceVersion = (await inventory.GetBalanceAsync(Input.ProductId, locationId, token)).Version;
            replacement = new(Input.Type, [new(Input.ProductId, Input.Quantity, Input.SourceLocationId, Input.DestinationLocationId, Input.LocationId, Input.ExpectedBalanceVersion)], Input.Reference, Input.Notes);
        }
        var result = await corrections.ConfirmAsync(new(Input.OperationId, id, requestedBy, Input.Pin, Input.Reason, replacement), token);
        Input.Pin = string.Empty;
        if (result.Status == InventoryCorrectionStatus.Success && result.ReversalMovementId is Guid reversal)
            return RedirectToPage("Details", new { id = result.ReplacementMovementId ?? reversal });
        ModelState.AddModelError(string.Empty, Message(result)); return Page();
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken token)
    {
        var service = HttpContext.RequestServices.GetRequiredService<InventoryHistoryService>();
        var movement = await service.GetAsync(id, token); if (movement is null) return false;
        Movement = movement; CanReplace = movement.Lines.Count == 1 && movement.OriginalCorrection is null && movement.ReversalCorrection is null;
        return true;
    }
    private static string Message(InventoryCorrectionResult result) => result.Status switch
    {
        InventoryCorrectionStatus.InvalidPin => "NIP inválido o sin permiso administrativo.",
        InventoryCorrectionStatus.AlreadyCorrected => "El movimiento ya fue corregido.",
        InventoryCorrectionStatus.CannotCorrectReversal => "No se puede corregir un movimiento de reverso.",
        InventoryCorrectionStatus.RequiresLocationSharingConfirmation => "La ubicación comparte pallet; confirme desde una captura operativa compatible.",
        InventoryCorrectionStatus.BalanceChanged => "El saldo cambió mientras se corregía. Recargue y confirme de nuevo.",
        InventoryCorrectionStatus.IdempotencyConflict => "El UUID ya fue usado con datos distintos.",
        _ => result.ValidationErrors.FirstOrDefault() ?? "No fue posible confirmar la corrección."
    };
    public sealed class InputModel
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        [Required] public string Reason { get; set; } = string.Empty;
        [Required, RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = string.Empty;
        public bool CreateReplacement { get; set; }
        public InventoryMovementType Type { get; set; }
        public Guid ProductId { get; set; }
        public decimal Quantity { get; set; }
        public Guid? SourceLocationId { get; set; }
        public Guid? DestinationLocationId { get; set; }
        public Guid? LocationId { get; set; }
        public uint? ExpectedBalanceVersion { get; set; }
        public string? Reference { get; set; }
        public string? Notes { get; set; }
        public static InputModel From(InventoryMovementLine line, InventoryMovementType type)
        {
            var change = line.BalanceChanges.SingleOrDefault();
            return new() { Type = type, ProductId = line.ProductId, Quantity = line.Quantity, SourceLocationId = line.SourceLocationId, DestinationLocationId = line.DestinationLocationId, LocationId = type == InventoryMovementType.Adjustment ? change?.LocationId : null, ExpectedBalanceVersion = type == InventoryMovementType.Adjustment ? 0u : null };
        }
    }
}
