using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Web.Localization;
using WarehouseEPI.Web.Security;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class DetailsModel(CycleCountService cycleCountService, CycleCountOperatorSession operatorSessions, IStringLocalizer<OperationsTexts> texts) : PageModel
{
    public CycleCountCampaignDetail? Campaign { get; private set; }
    public CycleCountOperatorSessionView? OperatorSession { get; private set; }
    [BindProperty] public string Pin { get; set; } = string.Empty;
    [BindProperty] public string OperatorPin { get; set; } = string.Empty;
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public string? ScannedLocationCode { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        await LoadAsync(id, renewOperatorSession: true, cancellationToken);
        return Campaign is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostStartOperatorSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        var pin = OperatorPin;
        OperatorPin = string.Empty;
        Campaign = await cycleCountService.GetCampaignAsync(id, cancellationToken);
        if (Campaign is null) return NotFound();
        OperatorSession = await operatorSessions.StartAsync(HttpContext, id, pin, cancellationToken);
        if (OperatorSession is not null) return RedirectToPage(new { id, scanNext = true });
        Error = texts["No fue posible validar el NIP."];
        return Page();
    }

    public IActionResult OnPostEndOperatorSession(Guid id)
    {
        operatorSessions.Clear(HttpContext);
        return RedirectToPage(new { id });
    }
    public async Task<IActionResult> OnPostReleaseAsync(Guid id, Guid operationId, CancellationToken cancellationToken) => await ExecuteAsync(id, () => cycleCountService.ReleaseAsync(id, operationId == Guid.Empty ? Guid.NewGuid() : operationId, Pin, cancellationToken), cancellationToken);
    public async Task<IActionResult> OnPostCancelAsync(Guid id, Guid operationId, CancellationToken cancellationToken) => await ExecuteAsync(id, () => cycleCountService.CancelAsync(id, operationId == Guid.Empty ? Guid.NewGuid() : operationId, Pin, Notes, cancellationToken), cancellationToken);
    public async Task<IActionResult> OnPostScanLocationAsync(Guid id, CancellationToken cancellationToken)
    {
        Campaign = await cycleCountService.GetCampaignAsync(id, cancellationToken);
        if (Campaign is null) return NotFound();
        OperatorSession = await operatorSessions.GetAsync(HttpContext, id, renew: true, cancellationToken);
        if (OperatorSession is null)
        {
            Error = texts["Identifícate con tu NIP para comenzar o continuar el conteo."];
            return Page();
        }
        var location = Campaign?.Locations.SingleOrDefault(item => string.Equals(item.LocationCode, ScannedLocationCode?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (location is null) { Error = texts["La ubicación escaneada no pertenece a esta campaña."]; return Page(); }
        return location.Status switch
        {
            WarehouseEPI.Core.Entities.CycleCountLocationStatus.Pending or WarehouseEPI.Core.Entities.CycleCountLocationStatus.RecountRequested or WarehouseEPI.Core.Entities.CycleCountLocationStatus.Stale => RedirectToPage("Count", new { id, locationId = location.Id }),
            WarehouseEPI.Core.Entities.CycleCountLocationStatus.Counting when location.ActiveAttemptId is Guid attemptId => RedirectToPage("Count", new { id, locationId = location.Id, attemptId }),
            WarehouseEPI.Core.Entities.CycleCountLocationStatus.UnderReview => RedirectToPage("Review", new { id, locationId = location.Id }),
            _ => RedirectToPage("Details", new { id })
        };
    }
    private async Task<IActionResult> ExecuteAsync(Guid id, Func<Task<CycleCountResult>> action, CancellationToken cancellationToken)
    { var result = await action(); Pin = string.Empty; if (result.Status == CycleCountStatus.Success) return RedirectToPage(new { id }); await LoadAsync(id, renewOperatorSession: false, cancellationToken); Error = result.Status == CycleCountStatus.InvalidPin ? texts["NIP no válido."] : string.Join(' ', result.ValidationErrors.Select(error => texts[error].Value)); return Page(); }

    private async Task LoadAsync(Guid id, bool renewOperatorSession, CancellationToken cancellationToken)
    {
        Campaign = await cycleCountService.GetCampaignAsync(id, cancellationToken);
        if (Campaign is not null)
            OperatorSession = await operatorSessions.GetAsync(HttpContext, id, renewOperatorSession, cancellationToken);
        Response.Headers.CacheControl = "no-store";
    }
}
