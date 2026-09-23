using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations.Map;

[Authorize(Policy = "AdminOnly")]
public sealed class CalibrationModel(WarehouseMapService maps, WarehouseMapCalibrationService calibrations,
    IStringLocalizer<CatalogTexts> text) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    [BindProperty] public InputModel Input { get; set; } = new();
    public WarehouseMapView Map { get; private set; } = new(0, 0, false, [], [], 0, 0, 0, 0, 0, [], [], [], false, null, "IMPERIAL");
    public WarehouseMapCalibrationState Calibration { get; private set; } = new(false, false, 0, 0, null, null, []);
    public WarehouseMapCalibrationReview? Review { get; private set; }
    public bool ClearDraft { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public bool ClearCalibrationDraft { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken token)
    {
        await LoadAsync(token);
        ClearDraft = ClearCalibrationDraft;
        if (!Map.IsInitialized) return RedirectToPage("Edit");
        Input = new()
        {
            OperationId = Guid.NewGuid(),
            ExpectedRevision = Calibration.Revision,
            PointsJson = JsonSerializer.Serialize(Calibration.Points)
        };
        return Page();
    }

    public async Task<IActionResult> OnPostReviewAsync(CancellationToken token)
    {
        await LoadAsync(token);
        var points = ReadPoints();
        if (points is not null) Review = await calibrations.ReviewAsync(points, token);
        foreach (var error in Review?.Errors ?? []) ModelState.AddModelError(string.Empty, error);
        Input.Pin = string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostPublishAsync(CancellationToken token)
    {
        await LoadAsync(token);
        var points = ReadPoints();
        if (points is null) return Page();
        var result = await calibrations.PublishAsync(new(Input.OperationId, CurrentUserId(), Input.ExpectedRevision,
            Input.Pin, Input.Reason, points), token);
        if (result.Status == WarehouseMapCalibrationSaveStatus.Success)
        {
            Message = text["Calibración publicada. Ya puede usarse Mi ubicación en el croquis."].Value;
            ClearCalibrationDraft = true;
            return RedirectToPage();
        }
        AddResultError(result);
        Input.Pin = string.Empty;
        Review = await calibrations.ReviewAsync(points, token);
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken token)
    {
        await LoadAsync(token);
        var result = await calibrations.DisableAsync(Input.OperationId, CurrentUserId(), Input.ExpectedRevision,
            Input.Pin, Input.Reason, token);
        if (result.Status == WarehouseMapCalibrationSaveStatus.Success)
        {
            Message = text["Calibración desactivada."].Value;
            ClearCalibrationDraft = true;
            return RedirectToPage();
        }
        AddResultError(result);
        Input.Pin = string.Empty;
        return Page();
    }

    private async Task LoadAsync(CancellationToken token)
    {
        Map = await maps.GetAsync(includeProposal: false, token);
        Calibration = await calibrations.GetStateAsync(includePoints: true, token);
    }

    private IReadOnlyList<WarehouseMapCalibrationPointInput>? ReadPoints()
    {
        try
        {
            return JsonSerializer.Deserialize<WarehouseMapCalibrationPointInput[]>(Input.PointsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            ModelState.AddModelError(string.Empty, text["El borrador de calibración no es válido. Recarga la página y vuelve a capturar los puntos."].Value);
            return null;
        }
    }

    private void AddResultError(WarehouseMapCalibrationSaveResult result)
    {
        var fallback = result.Status switch
        {
            WarehouseMapCalibrationSaveStatus.InvalidPin => "El NIP ADMIN no es válido.",
            WarehouseMapCalibrationSaveStatus.Unauthorized => "No tienes permiso para publicar la calibración.",
            WarehouseMapCalibrationSaveStatus.Conflict => "La calibración cambió mientras estaba abierta. Recarga antes de continuar.",
            WarehouseMapCalibrationSaveStatus.IdempotencyConflict => "La operación ya fue utilizada con datos distintos.",
            WarehouseMapCalibrationSaveStatus.NotInitialized => "Guarda el croquis antes de calibrar la ubicación.",
            _ => "No fue posible guardar la calibración."
        };
        foreach (var error in result.Errors?.DefaultIfEmpty(fallback) ?? [fallback])
            ModelState.AddModelError(string.Empty, text[error].Value);
    }

    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : Guid.Empty;

    public sealed class InputModel
    {
        public Guid OperationId { get; set; }
        public int ExpectedRevision { get; set; }
        public string PointsJson { get; set; } = "[]";
        public string Reason { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
    }
}
