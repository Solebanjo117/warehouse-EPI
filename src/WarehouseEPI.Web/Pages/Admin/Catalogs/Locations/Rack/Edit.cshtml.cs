using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Locations;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations.Rack;

[Authorize(Policy = "AdminOnly")]
public sealed class EditModel(LocationRackAdministrationService racks) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public LocationRackEditView Rack { get; private set; } = null!;
    public LocationRackEditSummary? ReviewSummary { get; private set; }
    public IReadOnlyList<string> ReviewErrors { get; private set; } = [];
    public bool IsReviewed { get; private set; }
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(string rowCode, short rackNumber,
        CancellationToken token)
    {
        var rack = await racks.GetAsync(rowCode, rackNumber, token);
        if (rack is null) return NotFound();
        Rack = rack;
        Input = new InputModel
        {
            OperationId = Guid.NewGuid(),
            RowCode = rack.RowCode,
            RackNumber = rack.RackNumber,
            PresentPallets = rack.Positions.Where(item => item.IsPhysicallyPresent)
                .Select(item => item.PalletNumber).ToArray()
        };
        return Page();
    }

    public async Task<IActionResult> OnPostReviewAsync(CancellationToken token)
    {
        if (!await LoadRackAsync(token)) return NotFound();
        var result = await racks.ReviewAsync(Command(pin: null), token);
        ReviewErrors = result.Errors;
        ReviewSummary = result.Summary;
        IsReviewed = result.Errors.Count == 0;
        Input.Pin = string.Empty;
        ModelState.Remove($"{nameof(Input)}.{nameof(InputModel.Pin)}");
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken token)
    {
        if (!await LoadRackAsync(token)) return NotFound();
        var result = await racks.SaveAsync(Command(Input.Pin), token);
        Input.Pin = string.Empty;
        ModelState.Remove($"{nameof(Input)}.{nameof(InputModel.Pin)}");
        if (result.Status == LocationRackSaveStatus.Success)
        {
            Message = $"Se actualizó la configuración física de {Input.RowCode}-{Input.RackNumber}.";
            return RedirectToPage(new { rowCode = Input.RowCode, rackNumber = Input.RackNumber });
        }
        ReviewErrors = result.Status switch
        {
            LocationRackSaveStatus.InvalidPin => ["No fue posible validar el NIP de un ADMIN activo."],
            LocationRackSaveStatus.Unauthorized => ["La sesión ADMIN ya no es válida."],
            LocationRackSaveStatus.IdempotencyConflict => ["La operación ya fue utilizada con otro contenido."],
            _ => result.Errors ?? ["No fue posible guardar la configuración del rack."]
        };
        ReviewSummary = (await racks.ReviewAsync(Command(pin: null), token)).Summary;
        IsReviewed = false;
        return Page();
    }

    private async Task<bool> LoadRackAsync(CancellationToken token)
    {
        var rack = await racks.GetAsync(Input.RowCode, Input.RackNumber, token);
        if (rack is null) return false;
        Rack = rack;
        return true;
    }

    private LocationRackEditCommand Command(string? pin) => new(Input.OperationId,
        CurrentUserId(), Input.RowCode, Input.RackNumber, Input.PresentPallets, Input.Reason, pin);

    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : Guid.Empty;

    public sealed class InputModel
    {
        public Guid OperationId { get; set; }
        public string RowCode { get; set; } = string.Empty;
        public short RackNumber { get; set; }
        public short[] PresentPallets { get; set; } = [];
        public string? Reason { get; set; }
        public string Pin { get; set; } = string.Empty;
    }
}
