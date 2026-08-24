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
    public WarehouseMapView Map { get; private set; } = new(0, 0, false, [], [], 0, 0, 0, 0, 0, [], [], [], false, null, "IMPERIAL");
    public string? PreviewToken { get; private set; }
    public IReadOnlyList<WarehouseMapRevisionView> Revisions { get; private set; } = [];
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken token)
    {
        Map = await maps.GetAsync(true, token); Input = new()
        {
            OperationId = Guid.NewGuid(),
            GeometryJson = JsonSerializer.Serialize(Map.Elements.Concat(Map.Unplaced).Select(ToGeometry)),
            ArchitectureJson = JsonSerializer.Serialize(Map.Architecture.Concat(Map.ArchivedArchitecture).Select(ToArchitectureItem)),
            LayerStateJson = JsonSerializer.Serialize(Map.Layers.Select(item => new WarehouseMapLayerState(item.Code, item.IsLocked))),
            ScaleUnitsPerInch = Map.ScaleUnitsPerInch,
            MeasurementSystem = Map.MeasurementSystem
        };
        if (!Map.IsInitialized) PreviewToken = previews.Save(CurrentUserId()).Token; else Revisions = await maps.GetRevisionsAsync(token: token);
    }

    public async Task<IActionResult> OnPostInitializeAsync(string? previewToken, CancellationToken token)
    {
        if (!previews.Consume(previewToken, CurrentUserId())) { ModelState.AddModelError(string.Empty, "La vista previa expiró, fue utilizada o no pertenece a esta sesión."); await ReloadAsync(token); return Page(); }
        IReadOnlyList<WarehouseMapGeometry> geometry;
        try { geometry = JsonSerializer.Deserialize<WarehouseMapGeometry[]>(Input.GeometryJson) ?? []; }
        catch (JsonException) { geometry = []; ModelState.AddModelError(string.Empty, "La geometría recibida no es válida."); }
        var architecture = DeserializeArchitecture();
        var layers = DeserializeLayers();
        if (!ModelState.IsValid) { await ReloadAsync(token); return Page(); }
        var result = await maps.InitializeAsync(Input.OperationId, CurrentUserId(), Input.Pin, Input.Reason, geometry,
            layers, architecture, Input.ScaleUnitsPerInch, Input.MeasurementSystem, token); Input.Pin = string.Empty; ModelState.Remove("Input.Pin");
        return await CompleteAsync(result, "Croquis inicial confirmado.", token);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken token)
    {
        IReadOnlyList<WarehouseMapGeometry> geometry;
        try { geometry = JsonSerializer.Deserialize<WarehouseMapGeometry[]>(Input.GeometryJson) ?? []; }
        catch (JsonException) { geometry = []; ModelState.AddModelError(string.Empty, "La geometría recibida no es válida."); }
        if (!ModelState.IsValid) { await ReloadAsync(token); return Page(); }
        var architecture = DeserializeArchitecture();
        var layers = DeserializeLayers();
        if (!ModelState.IsValid) { await ReloadAsync(token); return Page(); }
        var result = await maps.SaveAsync(new(Input.OperationId, CurrentUserId(), Input.Pin, Input.Reason, geometry,
            layers, architecture, Input.ScaleUnitsPerInch, Input.MeasurementSystem), token); Input.Pin = string.Empty; ModelState.Remove("Input.Pin");
        return await CompleteAsync(result, "Cambios del croquis guardados.", token);
    }

    public async Task<IActionResult> OnPostReviewAsync(CancellationToken token)
    {
        IReadOnlyList<WarehouseMapGeometry> geometry;
        try { geometry = JsonSerializer.Deserialize<WarehouseMapGeometry[]>(Input.GeometryJson) ?? []; }
        catch (JsonException) { return new JsonResult(new { errors = new[] { "La geometría recibida no es válida." } }) { StatusCode = 400 }; }
        var architecture = DeserializeArchitecture();
        var layers = DeserializeLayers();
        if (!ModelState.IsValid)
            return new JsonResult(new { errors = ModelState.Values.SelectMany(item => item.Errors).Select(item => item.ErrorMessage) }) { StatusCode = 400 };
        var review = await maps.ReviewAsync(new(Input.OperationId, CurrentUserId(), string.Empty, Input.Reason,
            geometry, layers, architecture, Input.ScaleUnitsPerInch, Input.MeasurementSystem), token);
        return new JsonResult(review) { StatusCode = review.Errors.Count == 0 ? 200 : 400 };
    }

    private async Task<IActionResult> CompleteAsync(WarehouseMapSaveResult result, string message, CancellationToken token)
    {
        if (result.Status == WarehouseMapSaveStatus.Success) { Message = message; return RedirectToPage("/Admin/Catalogs/Locations/Index", new { viewMode = "map" }); }
        var error = result.Status switch { WarehouseMapSaveStatus.InvalidPin => "NIP inválido o sin permiso ADMIN.", WarehouseMapSaveStatus.Conflict => "El croquis ya fue inicializado. Vuelve a abrir el editor.", WarehouseMapSaveStatus.IdempotencyConflict => "El UUID ya fue usado con datos diferentes.", WarehouseMapSaveStatus.NotInitialized => "Primero confirma la distribución inicial.", WarehouseMapSaveStatus.Unauthorized => "La sesión ADMIN ya no es válida.", _ => "No fue posible guardar el croquis." };
        foreach (var item in result.ValidationErrors.DefaultIfEmpty(error)) ModelState.AddModelError(string.Empty, item); await ReloadAsync(token); return Page();
    }

    private async Task ReloadAsync(CancellationToken token)
    {
        Map = await maps.GetAsync(true, token); if (!Map.IsInitialized) PreviewToken = previews.Save(CurrentUserId()).Token; else Revisions = await maps.GetRevisionsAsync(token: token);
        if (!string.IsNullOrWhiteSpace(Input.GeometryJson))
        {
            try { var draft = (JsonSerializer.Deserialize<WarehouseMapGeometry[]>(Input.GeometryJson) ?? []).ToDictionary(item => item.Id); Map = Map with { Elements = Map.Elements.Select(item => ApplyGeometry(item, draft)).ToArray(), Unplaced = Map.Unplaced.Select(item => ApplyGeometry(item, draft)).ToArray() }; } catch (JsonException) { Input.GeometryJson = string.Empty; }
        }
        if (!string.IsNullOrWhiteSpace(Input.ArchitectureJson))
        {
            try
            {
                var draft = (JsonSerializer.Deserialize<WarehouseMapArchitectureItem[]>(Input.ArchitectureJson) ?? [])
                    .ToDictionary(item => item.Id);
                var current = Map.Architecture.Concat(Map.ArchivedArchitecture).ToArray();
                var currentIds = current.Select(item => item.Id).ToHashSet();
                var merged = current.Select(item => ApplyArchitectureItem(item, draft))
                    .Concat(draft.Values.Where(item => !currentIds.Contains(item.Id)).Select(ToArchitectureView))
                    .OrderBy(item => item.ZIndex).ToArray();
                Map = Map with { Architecture = merged.Where(item => !item.IsArchived).ToArray(), ArchivedArchitecture = merged.Where(item => item.IsArchived).ToArray() };
            }
            catch (JsonException) { Input.ArchitectureJson = string.Empty; }
        }
        if (!string.IsNullOrWhiteSpace(Input.LayerStateJson))
        {
            try
            {
                var draft = (JsonSerializer.Deserialize<WarehouseMapLayerState[]>(Input.LayerStateJson) ?? [])
                    .ToDictionary(item => item.Code, StringComparer.Ordinal);
                Map = Map with { Layers = Map.Layers.Select(item => draft.TryGetValue(item.Code, out var state) ? item with { IsLocked = state.IsLocked } : item).ToArray() };
            }
            catch (JsonException) { Input.LayerStateJson = string.Empty; }
        }
        Input.OperationId = Guid.NewGuid();
        ModelState.Remove($"{nameof(Input)}.{nameof(InputModel.OperationId)}");
        if (string.IsNullOrWhiteSpace(Input.GeometryJson)) Input.GeometryJson = JsonSerializer.Serialize(Map.Elements.Concat(Map.Unplaced).Select(ToGeometry));
        if (string.IsNullOrWhiteSpace(Input.ArchitectureJson)) Input.ArchitectureJson = JsonSerializer.Serialize(Map.Architecture.Concat(Map.ArchivedArchitecture).Select(ToArchitectureItem));
        if (string.IsNullOrWhiteSpace(Input.LayerStateJson)) Input.LayerStateJson = JsonSerializer.Serialize(Map.Layers.Select(item => new WarehouseMapLayerState(item.Code, item.IsLocked)));
    }
    private IReadOnlyList<WarehouseMapArchitectureItem> DeserializeArchitecture()
    {
        try { return JsonSerializer.Deserialize<WarehouseMapArchitectureItem[]>(Input.ArchitectureJson) ?? []; }
        catch (JsonException) { ModelState.AddModelError(string.Empty, "La geometría arquitectónica recibida no es válida."); return []; }
    }
    private IReadOnlyList<WarehouseMapLayerState> DeserializeLayers()
    {
        try { return JsonSerializer.Deserialize<WarehouseMapLayerState[]>(Input.LayerStateJson) ?? []; }
        catch (JsonException) { ModelState.AddModelError(string.Empty, "La configuración de capas recibida no es válida."); return []; }
    }
    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private static WarehouseMapGeometry ToGeometry(WarehouseMapElementView item) => new(item.Id, item.X, item.Y, item.Width, item.Height, item.Rotation, item.ZIndex, item.IsVisible);
    private static WarehouseMapArchitectureItem ToArchitectureItem(WarehouseMapArchitecturalElementView item) =>
        new(item.Id, item.LayerCode, item.Kind, item.Label, item.X, item.Y, item.Width, item.Height, item.Rotation,
            item.CornerRadius, item.Points, item.StrokeToken, item.FillToken, item.StrokeWidth, item.IsDashed,
            item.ZIndex, item.IsLocked, item.GroupId, item.IsArchived);
    private static WarehouseMapElementView ApplyGeometry(WarehouseMapElementView item, IReadOnlyDictionary<Guid, WarehouseMapGeometry> draft) => draft.TryGetValue(item.Id, out var geometry) ? item with { X = geometry.X, Y = geometry.Y, Width = geometry.Width, Height = geometry.Height, Rotation = geometry.Rotation, ZIndex = geometry.ZIndex, IsVisible = geometry.IsVisible } : item;
    private static WarehouseMapArchitecturalElementView ApplyArchitectureItem(WarehouseMapArchitecturalElementView item, IReadOnlyDictionary<Guid, WarehouseMapArchitectureItem> draft) =>
        draft.TryGetValue(item.Id, out var value) ? ToArchitectureView(value) : item;
    private static WarehouseMapArchitecturalElementView ToArchitectureView(WarehouseMapArchitectureItem item) =>
        new(item.Id, item.LayerCode, item.Kind, item.Label, item.X, item.Y, item.Width, item.Height, item.Rotation,
            item.CornerRadius, item.Points, item.StrokeToken, item.FillToken, item.StrokeWidth, item.IsDashed,
            item.ZIndex, item.IsLocked, item.GroupId, item.IsArchived);
    public sealed class InputModel { public Guid OperationId { get; set; } public string GeometryJson { get; set; } = "[]"; public string ArchitectureJson { get; set; } = "[]"; public string LayerStateJson { get; set; } = "[]"; public decimal? ScaleUnitsPerInch { get; set; } public string MeasurementSystem { get; set; } = "IMPERIAL"; public string? Reason { get; set; } public string Pin { get; set; } = string.Empty; }
}
