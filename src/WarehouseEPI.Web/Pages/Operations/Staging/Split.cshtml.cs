using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.Staging;

public sealed class SplitModel(StagingArrivalQuery arrivals, PalletTrackingService tracking,
    WarehouseDbContext db, IStringLocalizer<OperationsTexts> texts) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid ArrivalLineId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? PlateId { get; set; }
    [BindProperty] public Guid OperationId { get; set; }
    [BindProperty] public string ArrivalVersion { get; set; } = "";
    [BindProperty] public long ExpectedVersion { get; set; }
    [BindProperty] public PalletDistributionInput Distribution { get; set; } = new() { Enabled = true };
    [BindProperty, Required, RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = "";
    public StagingArrivalRow Arrival { get; private set; } = null!;
    public IReadOnlyList<PalletSelection> Plates { get; private set; } = [];
    public PalletSelection? Selected => Plates.SingleOrDefault(p => p.PlateId == PlateId);
    public bool AllowsDecimals { get; private set; }
    public IReadOnlyList<Guid> NewPlates { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid? completed, CancellationToken token)
    {
        if (!await LoadAsync(token)) return NotFound();
        if (completed.HasValue)
            NewPlates = await db.PalletPlateEvents.AsNoTracking().Where(e => e.OperationId == completed &&
                e.MovementLineId == ArrivalLineId && e.Kind == "StagingSplitChild").OrderBy(e => e.PlateId).Select(e => e.PlateId).ToListAsync(token);
        if (!PlateId.HasValue && Plates.Count == 1) PlateId = Plates[0].PlateId;
        OperationId = Guid.NewGuid(); ArrivalVersion = Arrival.Version;
        ExpectedVersion = Selected?.ExpectedVersion ?? 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        if (!await LoadAsync(token)) return NotFound();
        if (ModelState.IsValid)
        {
            var result = await tracking.SplitStagingPlateAsync(new(OperationId, ArrivalLineId, PlateId ?? Guid.Empty,
                ExpectedVersion, ArrivalVersion, Distribution.Quantities.Select(q => q ?? 0).ToArray(), Pin), token);
            if (result.Status == InventoryMovementStatus.Success)
                return RedirectToPage(new { arrivalLineId = ArrivalLineId, completed = OperationId });
            var message = result.Status switch {
                InventoryMovementStatus.InvalidPin or InventoryMovementStatus.RoleNotAllowed => "NIP inválido o usuario sin permiso operativo.",
                InventoryMovementStatus.BalanceChanged => "La placa o la llegada cambió. Recarga antes de dividir.",
                InventoryMovementStatus.IdempotencyConflict => "La operación ya fue usada con datos distintos.",
                _ => result.ValidationErrors.FirstOrDefault() ?? "No fue posible dividir la placa. Consulta nuevamente."
            };
            ModelState.AddModelError(string.Empty, texts[message]);
        }
        if (ModelState.TryGetValue(nameof(Pin), out var pinState) && pinState.Errors.Count > 0)
            ModelState.AddModelError(string.Empty, texts["NIP inválido o usuario sin permiso operativo."]);
        Pin = ""; ModelState.Remove(nameof(Pin));
        return Page();
    }

    private async Task<bool> LoadAsync(CancellationToken token)
    {
        var row = await arrivals.GetAsync(ArrivalLineId, token);
        if (row is null) return false;
        Arrival = row;
        Plates = row.NeedsIdentification ? [] : StagingArrivalQuery.DecodeVersion(row.Version) ?? [];
        AllowsDecimals = await db.Products.Where(p => p.Id == row.ProductId).Select(p => p.BaseUnit.AllowsDecimals).SingleAsync(token);
        return true;
    }
}
