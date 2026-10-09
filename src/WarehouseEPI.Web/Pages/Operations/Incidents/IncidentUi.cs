using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Operations.Incidents;

public static class IncidentUi
{
    public static string Label(object value) => value switch {
        MaterialIncidentStatus.Open => "Abierta", MaterialIncidentStatus.Reviewing => "En revisión", MaterialIncidentStatus.Resolved => "Resuelta", MaterialIncidentStatus.Voided => "Anulada",
        MaterialIncidentScope.Receiving => "Recepción", MaterialIncidentScope.Warehouse => "Almacén", MaterialIncidentScope.Undetermined => "Por determinar",
        MaterialIncidentKind.Damage => "Daño", MaterialIncidentKind.QuantityDifference => "Diferencia de cantidad", MaterialIncidentKind.WrongProduct => "Producto incorrecto", MaterialIncidentKind.Unidentified => "Falta de identificación", MaterialIncidentKind.Other => "Otro",
        MaterialIncidentDifference.Shortage => "Faltante", MaterialIncidentDifference.Surplus => "Sobrante", MaterialIncidentDifference.Undetermined => "Por determinar",
        "Report" => "Reportar incidencia", "Comment" => "Agregar seguimiento", "Review" => "Pasar a revisión", "Resolve" => "Resolver (ADMIN)", "Void" => "Anular (ADMIN)", "Reopen" => "Reabrir (ADMIN)", _ => value.ToString() ?? ""
    };
}

public abstract class IncidentCaptureModel : PageModel
{
    [BindProperty] public List<IFormFile> Photos { get; set; } = [];
    [BindProperty] public string Pin { get; set; } = "";
    public bool ReselectPhotos { get; protected set; }
    protected async Task<IReadOnlyList<IncidentPhotoInput>> ReadPhotosAsync(CancellationToken ct)
    {
        var result = new List<IncidentPhotoInput>();
        if (Photos.Count > 5) { ModelState.AddModelError("Photos", "Máximo 5 fotografías por incidencia."); return result; }
        foreach (var photo in Photos)
        {
            if (photo.Length is < 1 or > MaterialIncidentService.MaxPhotoBytes) { ModelState.AddModelError("Photos", "Cada fotografía debe pesar como máximo 5 MiB."); continue; }
            using var bytes = new MemoryStream(); await photo.CopyToAsync(bytes, ct);
            result.Add(new(Path.GetFileName(photo.FileName), photo.ContentType, bytes.ToArray()));
        }
        return result;
    }
    protected void ClearCredentials()
    {
        Pin = ""; ModelState.Remove(nameof(Pin)); ReselectPhotos = Photos.Count > 0; Photos.Clear();
    }
}
