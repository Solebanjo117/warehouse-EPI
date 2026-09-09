using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Reporting;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Movements;

[Authorize(Policy = "AdminOnly")]
public sealed class DetailsModel(
    InventoryHistoryService history,
    MovementTraceabilityService traceability,
    WarehouseClock clock,
    WarehouseSettingsService settings,
    TimeProvider timeProvider) : PageModel
{
    public InventoryMovementDetail Movement { get; private set; } = null!;
    public MovementTraceabilityContext Traceability { get; private set; } = new("Vigente", [], []);
    public BusinessSettingsSnapshot Business { get; private set; } = WarehouseSettingsService.Defaults();
    public Dictionary<string, DateTimeOffset> TraceDates { get; } = [];
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
    public DateTimeOffset GeneratedAt { get; private set; }
    public string? LogoUrl => Business.LogoFileName is null ? null : $"/branding/logo?v={Business.LogoHash}";
    public string ReturnUrl { get; private set; } = "/Admin/Inventory/Movements";
    public bool CanGeneratePalletPlate => PalletLicensePlateService.IsEligible(
        Movement.Type,
        Movement.Purpose,
        Movement.Lines.Count,
        Movement.OriginalCorrection is not null || Movement.ReversalCorrection is not null);

    public async Task<IActionResult> OnGetAsync(Guid id, string? returnUrl, CancellationToken token)
    {
        var movement = await history.GetAsync(id, token);
        if (movement is null) return NotFound();
        Movement = movement;
        Traceability = await traceability.GetAsync(id, token) ?? new("Vigente", [], []);
        Business = await settings.GetAsync(token);
        OccurredAt = await clock.ConvertAsync(movement.OccurredAt, token);
        RecordedAt = await clock.ConvertAsync(movement.RecordedAt, token);
        GeneratedAt = await clock.ConvertAsync(timeProvider.GetUtcNow(), token);
        foreach (var item in Traceability.Events)
            TraceDates[item.Id] = await clock.ConvertAsync(item.OccurredAt, token);
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Page("Index") ?? "/Admin/Inventory/Movements";
        return Page();
    }
}
