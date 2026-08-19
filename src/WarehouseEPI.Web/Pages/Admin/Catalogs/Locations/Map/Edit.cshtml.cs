using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Web.Locations;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations.Map;

[Authorize(Policy = "AdminOnly")]
public sealed class EditModel(WarehouseMapService maps, WarehouseMapPreviewStore previews) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public WarehouseMapView Map { get; private set; } = new(0, 0, false, [], [], 0, 0, 0, 0, 0);
    public string? PreviewToken { get; private set; }
    public IReadOnlyList<WarehouseMapRevisionView> Revisions { get; private set; } = [];
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken token)
    {
        Map = await maps.GetAsync(true, token); Input = new() { OperationId = Guid.NewGuid(), ExpectedVersion = Map.Version, GeometryJson = JsonSerializer.Serialize(Map.Elements.Concat(Map.Unplaced).Select(ToGeometry)) };
        if (!Map.IsInitialized) PreviewToken = previews.Save(CurrentUserId()).Token; else Revisions = await maps.GetRevisionsAsync(token: token);
    }

    public async Task<IActionResult> OnPostInitializeAsync(string? previewToken, CancellationToken token)
    {
        if (!previews.Consume(previewToken, CurrentUserId())) { ModelState.AddModelError(string.Empty, "La vista previa expiró, fue utilizada o no pertenece a esta sesión."); await ReloadAsync(token); return Page(); }
        IReadOnlyList<WarehouseMapGeometry> geometry;
        try { geometry = JsonSerializer.Deserialize<WarehouseMapGeometry[]>(Input.GeometryJson) ?? []; } catch (JsonException) { geometry = []; }
        var result = await maps.InitializeAsync(Input.OperationId, CurrentUserId(), Input.Pin, Input.Reason, geometry, token); Input.Pin = string.Empty; ModelState.Remove("Input.Pin");
        return await CompleteAsync(result, "Croquis inicial confirmado.", token);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken token)
    {
        IReadOnlyList<WarehouseMapGeometry> geometry;
        try { geometry = JsonSerializer.Deserialize<WarehouseMapGeometry[]>(Input.GeometryJson) ?? []; }
        catch (JsonException) { geometry = []; ModelState.AddModelError(string.Empty, "La geometría recibida no es válida."); }
        if (!ModelState.IsValid) { await ReloadAsync(token); return Page(); }
        var result = await maps.SaveAsync(new(Input.OperationId, CurrentUserId(), Input.Pin, Input.ExpectedVersion, Input.Reason, geometry), token); Input.Pin = string.Empty; ModelState.Remove("Input.Pin");
        return await CompleteAsync(result, "Cambios del croquis guardados.", token);
    }

    private async Task<IActionResult> CompleteAsync(WarehouseMapSaveResult result, string message, CancellationToken token)
    {
        if (result.Status == WarehouseMapSaveStatus.Success) { Message = message; return RedirectToPage("/Admin/Catalogs/Locations/Index", new { viewMode = "map" }); }
        var error = result.Status switch { WarehouseMapSaveStatus.InvalidPin => "NIP inválido o sin permiso ADMIN.", WarehouseMapSaveStatus.Conflict => "El croquis cambió mientras estaba abierto. Recarga la versión actual y vuelve a confirmar con NIP.", WarehouseMapSaveStatus.IdempotencyConflict => "El UUID ya fue usado con datos diferentes.", WarehouseMapSaveStatus.NotInitialized => "Primero confirma la distribución inicial.", WarehouseMapSaveStatus.Unauthorized => "La sesión ADMIN ya no es válida.", _ => "No fue posible guardar el croquis." };
        foreach (var item in result.ValidationErrors.DefaultIfEmpty(error)) ModelState.AddModelError(string.Empty, item); await ReloadAsync(token); return Page();
    }

    private async Task ReloadAsync(CancellationToken token)
    {
        Map = await maps.GetAsync(true, token); if (!Map.IsInitialized) PreviewToken = previews.Save(CurrentUserId()).Token; else Revisions = await maps.GetRevisionsAsync(token: token);
        if (!string.IsNullOrWhiteSpace(Input.GeometryJson))
        {
            try { var draft = (JsonSerializer.Deserialize<WarehouseMapGeometry[]>(Input.GeometryJson) ?? []).ToDictionary(item => item.Id); Map = Map with { Elements = Map.Elements.Select(item => ApplyGeometry(item, draft)).ToArray(), Unplaced = Map.Unplaced.Select(item => ApplyGeometry(item, draft)).ToArray() }; } catch (JsonException) { Input.GeometryJson = string.Empty; }
        }
        Input.OperationId = Guid.NewGuid(); Input.ExpectedVersion = Map.Version; if (string.IsNullOrWhiteSpace(Input.GeometryJson)) Input.GeometryJson = JsonSerializer.Serialize(Map.Elements.Concat(Map.Unplaced).Select(ToGeometry));
    }
    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private static WarehouseMapGeometry ToGeometry(WarehouseMapElementView item) => new(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible);
    private static WarehouseMapElementView ApplyGeometry(WarehouseMapElementView item, IReadOnlyDictionary<Guid, WarehouseMapGeometry> draft) => draft.TryGetValue(item.Id, out var geometry) ? item with { X = geometry.X, Y = geometry.Y, Width = geometry.Width, Height = geometry.Height, Rotation = geometry.Rotation, ZIndex = geometry.ZIndex, IsVisible = geometry.IsVisible } : item;
    public sealed class InputModel { public Guid OperationId { get; set; } public int ExpectedVersion { get; set; } public string GeometryJson { get; set; } = "[]"; public string? Reason { get; set; } public string Pin { get; set; } = string.Empty; }
}
