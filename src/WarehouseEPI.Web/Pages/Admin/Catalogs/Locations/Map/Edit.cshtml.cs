using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Web.Locations;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Locations.Map;

[Authorize(Policy = "AdminOnly")]
public sealed class EditModel(WarehouseMapService maps, WarehouseMapPreviewStore previews,
    WarehouseMapReferenceStorage referenceStorage) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public WarehouseMapView Map { get; private set; } = new(0, 0, false, [], [], 0, 0, 0, 0, 0, [], [], [], false, null, "IMPERIAL");
    public string? PreviewToken { get; private set; }
    public IReadOnlyList<WarehouseMapRevisionView> Revisions { get; private set; } = [];
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken token)
    {
        Map = await maps.GetAsync(true, token);
        await referenceStorage.CleanupExpiredAsync(token);
        await referenceStorage.CleanupUnreferencedAsync((Map.ActiveReference is null ? [] : new[] { Map.ActiveReference })
            .Concat(Map.ArchivedReferences ?? []).Select(item => item.StoredFileName).ToArray(), TimeSpan.FromDays(1), token);
        Input = new()
        {
            OperationId = Guid.NewGuid(),
            GeometryJson = JsonSerializer.Serialize(Map.Elements.Concat(Map.Unplaced).Select(ToGeometry)),
            ArchitectureJson = JsonSerializer.Serialize(Map.Architecture.Concat(Map.ArchivedArchitecture).Select(ToArchitectureItem)),
            LayerStateJson = JsonSerializer.Serialize(Map.Layers.Select(item => new WarehouseMapLayerState(item.Code, item.IsLocked))),
            ReferenceImageJson = JsonSerializer.Serialize((Map.ActiveReference is null ? [] : new[] { Map.ActiveReference })
                .Concat(Map.ArchivedReferences ?? [])),
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
        var references = await PrepareReferencesAsync(promote: true, token);
        if (!ModelState.IsValid) { await ReloadAsync(token); return Page(); }
        var result = await maps.SaveAsync(new(Input.OperationId, CurrentUserId(), Input.Pin, Input.Reason, geometry,
            layers, architecture, Input.ScaleUnitsPerInch, Input.MeasurementSystem, references), token); Input.Pin = string.Empty; ModelState.Remove("Input.Pin");
        return await CompleteAsync(result, "Cambios del croquis guardados.", token);
    }

    public async Task<IActionResult> OnPostReviewAsync(CancellationToken token)
    {
        IReadOnlyList<WarehouseMapGeometry> geometry;
        try { geometry = JsonSerializer.Deserialize<WarehouseMapGeometry[]>(Input.GeometryJson) ?? []; }
        catch (JsonException) { return new JsonResult(new { errors = new[] { "La geometría recibida no es válida." } }) { StatusCode = 400 }; }
        var architecture = DeserializeArchitecture();
        var layers = DeserializeLayers();
        var references = await PrepareReferencesAsync(promote: false, token);
        if (!ModelState.IsValid)
            return new JsonResult(new { errors = ModelState.Values.SelectMany(item => item.Errors).Select(item => item.ErrorMessage) }) { StatusCode = 400 };
        var review = await maps.ReviewAsync(new(Input.OperationId, CurrentUserId(), string.Empty, Input.Reason,
            geometry, layers, architecture, Input.ScaleUnitsPerInch, Input.MeasurementSystem, references), token);
        return new JsonResult(review) { StatusCode = review.Errors.Count == 0 ? 200 : 400 };
    }

    public async Task<IActionResult> OnPostUploadReferenceAsync(IFormFile? referenceImage, CancellationToken token)
    {
        if (referenceImage is null)
            return new JsonResult(new { error = "Selecciona una imagen PNG, JPEG o WebP." }) { StatusCode = 400 };
        try
        {
            var staged = await referenceStorage.StageAsync(referenceImage, CurrentUserId(), token);
            return new JsonResult(new
            {
                token = staged.Token,
                id = staged.ReferenceId,
                staged.OriginalFileName,
                staged.StoredFileName,
                staged.ContentType,
                staged.Sha256,
                staged.PixelWidth,
                staged.PixelHeight,
                previewUrl = Url.Page(null, null, new { handler = "ReferencePreview", token = staged.Token })
            });
        }
        catch (WarehouseMapReferenceValidationException exception)
        {
            return new JsonResult(new { error = exception.Message }) { StatusCode = 400 };
        }
    }

    public async Task<IActionResult> OnGetReferenceAsync(Guid id, CancellationToken token)
    {
        var reference = await maps.GetReferenceAsync(id, token);
        var path = referenceStorage.GetPath(reference?.StoredFileName);
        if (reference is null || path is null) return NotFound();
        Response.Headers.ETag = $"\"{reference.Sha256}\"";
        Response.Headers.CacheControl = "private,max-age=604800,immutable";
        return PhysicalFile(path, reference.ContentType);
    }

    public async Task<IActionResult> OnGetReferencePreviewAsync(Guid token, CancellationToken cancellationToken)
    {
        var staged = await referenceStorage.GetStageAsync(token, CurrentUserId(), cancellationToken);
        var path = staged is null ? null : await referenceStorage.GetStagePathAsync(token, CurrentUserId(), cancellationToken);
        if (staged is null || path is null) return NotFound();
        Response.Headers.CacheControl = "private,no-store";
        return PhysicalFile(path, staged.ContentType);
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
        if (!string.IsNullOrWhiteSpace(Input.ReferenceImageJson))
        {
            try
            {
                var draft = JsonSerializer.Deserialize<WarehouseMapReferenceImageState[]>(Input.ReferenceImageJson) ?? [];
                Map = Map with
                {
                    ActiveReference = draft.SingleOrDefault(item => !item.IsArchived),
                    ArchivedReferences = draft.Where(item => item.IsArchived).ToArray()
                };
            }
            catch (JsonException) { Input.ReferenceImageJson = "[]"; }
        }
        Input.OperationId = Guid.NewGuid();
        ModelState.Remove($"{nameof(Input)}.{nameof(InputModel.OperationId)}");
        if (string.IsNullOrWhiteSpace(Input.GeometryJson)) Input.GeometryJson = JsonSerializer.Serialize(Map.Elements.Concat(Map.Unplaced).Select(ToGeometry));
        if (string.IsNullOrWhiteSpace(Input.ArchitectureJson)) Input.ArchitectureJson = JsonSerializer.Serialize(Map.Architecture.Concat(Map.ArchivedArchitecture).Select(ToArchitectureItem));
        if (string.IsNullOrWhiteSpace(Input.LayerStateJson)) Input.LayerStateJson = JsonSerializer.Serialize(Map.Layers.Select(item => new WarehouseMapLayerState(item.Code, item.IsLocked)));
        if (string.IsNullOrWhiteSpace(Input.ReferenceImageJson)) Input.ReferenceImageJson = JsonSerializer.Serialize((Map.ActiveReference is null ? [] : new[] { Map.ActiveReference }).Concat(Map.ArchivedReferences ?? []));
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
    private async Task<IReadOnlyList<WarehouseMapReferenceImageState>> PrepareReferencesAsync(bool promote,
        CancellationToken token)
    {
        WarehouseMapReferenceImageState[] values;
        try { values = JsonSerializer.Deserialize<WarehouseMapReferenceImageState[]>(Input.ReferenceImageJson ?? "[]") ?? []; }
        catch (JsonException) { ModelState.AddModelError(string.Empty, "La referencia recibida no es válida."); return []; }
        var persisted = await maps.GetPersistedReferenceIdsAsync(token);
        var additions = values.Where(item => !persisted.Contains(item.Id)).ToArray();
        if (additions.Length == 0) return values;
        if (additions.Length != 1 || Input.ReferenceUploadToken is not Guid uploadToken)
        {
            ModelState.AddModelError(string.Empty, "La referencia nueva no tiene una carga temporal válida.");
            return values;
        }
        var staged = promote
            ? await referenceStorage.PromoteAsync(uploadToken, CurrentUserId(), token)
            : await referenceStorage.GetStageAsync(uploadToken, CurrentUserId(), token);
        if (staged is null || additions[0].Id != staged.ReferenceId)
        {
            ModelState.AddModelError(string.Empty, "La carga temporal expiró o no pertenece a esta sesión.");
            return values;
        }
        var submitted = additions[0];
        var normalized = submitted with
        {
            OriginalFileName = staged.OriginalFileName,
            StoredFileName = staged.StoredFileName,
            ContentType = staged.ContentType,
            Sha256 = staged.Sha256,
            PixelWidth = staged.PixelWidth,
            PixelHeight = staged.PixelHeight
        };
        return values.Select(item => item.Id == normalized.Id ? normalized : item).ToArray();
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
    public sealed class InputModel { public Guid OperationId { get; set; } public string GeometryJson { get; set; } = "[]"; public string ArchitectureJson { get; set; } = "[]"; public string LayerStateJson { get; set; } = "[]"; public string ReferenceImageJson { get; set; } = "[]"; public Guid? ReferenceUploadToken { get; set; } public decimal? ScaleUnitsPerInch { get; set; } public string MeasurementSystem { get; set; } = "IMPERIAL"; public string? Reason { get; set; } public string Pin { get; set; } = string.Empty; }
}
