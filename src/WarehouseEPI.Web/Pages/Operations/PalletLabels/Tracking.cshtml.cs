using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.PalletLabels;

public sealed class TrackingModel(PalletTrackingService tracking, WarehouseClock clock, IStringLocalizer<OperationsTexts> texts) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? OriginId { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty] public ActivationInput Activation { get; set; } = new();
    [BindProperty] public VoidInput Void { get; set; } = new();
    public IReadOnlyList<PalletQueryRow> Rows { get; private set; } = [];
    public IReadOnlyList<Location> Locations { get; private set; } = [];
    public IReadOnlyList<PalletOrderLink> Orders { get; private set; } = [];
    public List<(PalletPlateEvent Event, DateTimeOffset Local, PalletHistoryRow Detail)> History { get; } = [];
    public async Task OnGetAsync(Guid? id, CancellationToken token)
    {
        Locations = await tracking.LocationsAsync(token);
        if (id.HasValue) Search = id.ToString();
        Rows = await tracking.SearchAsync(Search, Status, OriginId, PageNumber, token);
        if (Rows.Count == 1)
        {
            Void.PlateId = Rows[0].Id; Void.ExpectedVersion = Rows[0].Version;
            Orders = await tracking.OrdersAsync(Rows[0].Id, token);
            foreach (var e in await tracking.HistoryDetailsAsync(Rows[0].Id, token)) History.Add((e.Event, await clock.ConvertAsync(e.Event.RecordedAt, token), e));
        }
    }
    public async Task<IActionResult> OnGetAvailableAsync(Guid productId, Guid locationId, bool blind, Guid? issueLinkId, CancellationToken token) =>
        new JsonResult(await tracking.AvailableAsync(productId, locationId, blind, issueLinkId, token));
    public async Task<IActionResult> OnPostActivateAsync(CancellationToken token)
    {
        if (!Guid.TryParse(Activation.Folio?.Replace("PLT-", "", StringComparison.OrdinalIgnoreCase), out var id))
            ModelState.AddModelError(string.Empty, texts["Introduce el folio de la entrada o placa documental."]);
        if (ModelState.IsValid)
        {
            var result = await tracking.ActivateAsync(new(Activation.OperationId, id, Activation.LocationId, Activation.Quantity, Activation.Pin), token);
            if (result.Status == InventoryMovementStatus.Success) return RedirectToPage(new { id });
            foreach (var error in result.ValidationErrors.DefaultIfEmpty("No fue posible validar la operación o el NIP.")) ModelState.AddModelError(string.Empty, texts[error]);
        }
        Activation.Pin = ""; ModelState.Remove("Activation.Pin"); await OnGetAsync(null, token); return Page();
    }
    public async Task<IActionResult> OnPostVoidAsync(CancellationToken token)
    {
        if (ModelState.IsValid)
        {
            var result = await tracking.VoidIdentificationAsync(new(Void.OperationId, Void.PlateId, Void.ExpectedVersion, Void.Reason, Void.Pin), token);
            if (result.Status == InventoryMovementStatus.Success) return RedirectToPage(new { id = Void.PlateId });
            foreach (var error in result.ValidationErrors.DefaultIfEmpty(result.Status == InventoryMovementStatus.InvalidPin
                         ? "NIP inválido o se requiere un usuario ADMIN." : "No fue posible anular la identificación."))
                ModelState.AddModelError(string.Empty, texts[error]);
        }
        Void.Pin = ""; ModelState.Remove("Void.Pin"); await OnGetAsync(Void.PlateId, token); return Page();
    }
    public sealed class ActivationInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public string? Folio { get; set; }
        public Guid LocationId { get; set; }
        public decimal Quantity { get; set; }
        public string Pin { get; set; } = "";
    }
    public sealed class VoidInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public Guid PlateId { get; set; }
        public long ExpectedVersion { get; set; }
        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(500, MinimumLength = 3)] public string Reason { get; set; } = "";
        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = "";
    }
}
