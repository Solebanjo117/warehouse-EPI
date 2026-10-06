using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Imports;
using WarehouseEPI.Web.Imports;

namespace WarehouseEPI.Web.Pages.Admin.Inventory;

[Authorize(Policy = "AdminOnly")]
[RequestSizeLimit(11 * 1024 * 1024)]
public sealed class WipImportModel(WipTransferPreviewStore store, WipTransferImportService importer) : PageModel
{
    public WipTransferDraft? Draft { get; private set; }
    public WipTransferReview? Review { get; private set; }
    public IReadOnlyList<WipTransferReviewRow> Rows { get; private set; } = [];
    public int CurrentPage { get; private set; }
    public int TotalPages { get; private set; }
    public string Filter { get; private set; } = "all";
    public string Search { get; private set; } = "";
    public int? Delivery { get; private set; }
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }
    public IReadOnlyList<string> ProductOptions { get; private set; } = [];
    public WipTransferReviewRow? EditRow { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid token, string filter = "all", int pageNumber = 1, int? editRow = null,
        string? search = null, int? delivery = null, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        if (!TryOwner(out var owner)) return Forbid();
        if (token == Guid.Empty) return Page();
        Draft = store.Get(token, owner);
        if (Draft is null) { Error = "La vista previa expiró o no te pertenece. Vuelve a cargar el archivo."; return RedirectToPage(); }
        Search = search?.Trim() ?? "";
        Delivery = delivery;
        From = from;
        To = to;
        ProductOptions = Draft.File.Rows.Select(x => x.Sku).Where(x => x.Length > 0).Distinct().Order().ToArray();
        var selection = WipTransferSelection.Select(Draft.File, Search, Delivery, from, to);
        if (!ModelState.IsValid || from > to)
        {
            Error = "Revisa las fechas: usa fechas válidas y un inicio anterior o igual al fin.";
            selection = selection with { Rows = [] };
        }
        Review = await importer.ReviewAsync(selection, Draft.Resolutions, ct: ct);
        EditRow = Review.Rows.SingleOrDefault(x => x.Source.Number == editRow && x.ExistingMovementId is null);
        Filter = filter is "errors" or "pending" or "excluded" or "imported" or "notImported" ? filter : "all";
        var rows = Review.Rows.Where(row => Filter switch
        {
            "errors" => row.Pending && row.Errors.Count > 0,
            "pending" => row.Pending,
            "excluded" => row.Resolution.Excluded,
            "imported" => row.ExistingMovementId.HasValue,
            "notImported" => !row.ExistingMovementId.HasValue,
            _ => true
        }).OrderBy(x => x.Source.Date).ThenBy(x => x.Source.Number).ToArray();
        TotalPages = Math.Max(1, (int)Math.Ceiling(rows.Length / 25d));
        CurrentPage = Math.Clamp(pageNumber, 1, TotalPages);
        Rows = rows.Skip((CurrentPage - 1) * 25).Take(25).ToArray();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? upload, CancellationToken ct)
    {
        if (!TryOwner(out var owner)) return Forbid();
        if (upload is null || upload.Length == 0 || upload.Length > WipTransferSpreadsheetReader.MaxBytes ||
            !string.Equals(Path.GetExtension(upload.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Selecciona un archivo XLSX de hasta 10 MB.";
            return RedirectToPage();
        }
        try
        {
            await using var stream = upload.OpenReadStream();
            var file = WipTransferSpreadsheetReader.Read(stream, upload.FileName);
            var review = await importer.ReviewAsync(file, new Dictionary<int, WipTransferResolution>(), suggestLocations: true, ct: ct);
            await using var guard = await store.LockAsync(ct);
            var draft = store.Create(owner, file, review.Rows.ToDictionary(x => x.Source.Number, x => x.Resolution));
            return RedirectToPage(new { token = draft.Token });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or FormatException)
        {
            Error = ex is InvalidDataException ? ex.Message : "No fue posible leer el XLSX. Revisa el formato del archivo.";
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostResolveAsync(Guid token, int revision, string scope, int? rowNumber,
        string? sku, string? area, Guid? sourceId, Guid? destinationId, bool excluded, string? reason,
        int pageNumber = 1, string filter = "all", string? search = null, int? delivery = null, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        if (!TryOwner(out var owner)) return Forbid();
        await using var guard = await store.LockAsync(ct);
        var draft = store.Get(token, owner);
        if (draft is null || draft.Revision != revision) return Stale(token, search, delivery, from, to);
        var selection = WipTransferSelection.Select(draft.File, search, delivery, from, to);
        var review = await importer.ReviewAsync(selection, draft.Resolutions, ct: ct);
        if (!ModelState.IsValid || from > to || (sourceId.HasValue && !review.Origins.Any(x => x.Id == sourceId)) ||
            (destinationId.HasValue && !review.Destinations.Any(x => x.Id == destinationId)) ||
            (scope == "row" && excluded && (string.IsNullOrWhiteSpace(reason) || reason.Length > 200)))
        {
            Error = "Selecciona ubicaciones disponibles y un motivo de hasta 200 caracteres para excluir.";
            return RedirectToPage(new { token, pageNumber, filter, search, delivery, from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") });
        }
        var targets = review.Rows.Where(x => x.ExistingMovementId is null && (scope switch
        {
            "row" => x.Source.Number == rowNumber,
            "sku" => !x.Resolution.Excluded && !string.IsNullOrEmpty(sku) && x.Source.Sku == sku,
            "skuMissing" => !x.Resolution.Excluded && x.Resolution.SourceId is null && !string.IsNullOrEmpty(sku) && x.Source.Sku == sku,
            "area" => !x.Resolution.Excluded && x.Source.Area == (area ?? ""),
            "errors" => !x.Resolution.Excluded && x.Errors.Count > 0,
            "all" => !x.Resolution.Excluded,
            _ => false
        })).ToArray();
        if (targets.Length == 0) { Error = "No hay filas pendientes que coincidan con la selección."; return RedirectToPage(new { token, search, delivery, from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") }); }
        if (sourceId.HasValue && targets.Any(row => !row.SourceOptions.Any(option => option.Id == sourceId.Value)))
        {
            Error = "El origen debe tener stock positivo del producto de cada fila y estar fuera de WIP. Revisa las ubicaciones disponibles.";
            return RedirectToPage(new { token, pageNumber, filter, search, delivery, from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") });
        }
        var resolutions = draft.Resolutions.ToDictionary();
        foreach (var row in targets)
            resolutions[row.Source.Number] = row.Resolution with
            {
                SourceId = sourceId ?? row.Resolution.SourceId,
                AutomaticSource = !sourceId.HasValue && row.Resolution.AutomaticSource,
                DestinationId = destinationId ?? row.Resolution.DestinationId,
                Excluded = scope == "row" ? excluded : row.Resolution.Excluded,
                ExclusionReason = scope == "row" ? reason?.Trim() : row.Resolution.ExclusionReason
            };
        store.Save(draft with { Resolutions = resolutions, Revision = draft.Revision + 1 });
        Message = $"Se actualizaron {targets.Length} filas. Revisa el efecto en existencias antes de confirmar.";
        return RedirectToPage(new { token, pageNumber, filter, search, delivery, from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") });
    }

    public async Task<IActionResult> OnPostConfirmAsync(Guid token, int revision, string? pin,
        bool reviewed, bool reviewedRepeatedRows, bool approveSharing, string? search = null, int? delivery = null, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        if (!TryOwner(out var owner)) return Forbid();
        await using var guard = await store.LockAsync(ct);
        var draft = store.Get(token, owner);
        if (draft is null || draft.Revision != revision) return Stale(token, search, delivery, from, to);
        if (!ModelState.IsValid || from > to || !reviewed) { Error = "Revisa y acepta el efecto en existencias antes de confirmar."; return RedirectToPage(new { token, search, delivery, from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") }); }
        var selection = WipTransferSelection.Select(draft.File, search, delivery, from, to);
        if (selection.Rows.Count == 0) { Error = "No hay entregas en la selección. Revisa el producto y la fila."; return RedirectToPage(new { token, search, delivery, from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") }); }
        var result = await importer.ConfirmAsync(selection, draft.Resolutions, pin ?? "", reviewedRepeatedRows, approveSharing, ct);
        if (!result.Success) { Error = result.Error; return RedirectToPage(new { token, search, delivery, from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") }); }
        // Keep the snapshot for reviewing completed movements; durable operation IDs prevent replay.
        store.Save(draft with { Revision = draft.Revision + 1 });
        Message = $"Importación terminada: {result.Imported} salidas registradas; {result.AlreadyImported} ya importadas. Se actualizó el almacén y el seguimiento documental WIP.";
        return RedirectToPage(new { token, search, delivery, from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") });
    }

    private IActionResult Stale(Guid token, string? search = null, int? delivery = null, DateOnly? from = null, DateOnly? to = null)
    {
        Error = "La vista previa cambió o expiró. Revisa la versión actual antes de continuar.";
        return RedirectToPage(new { token, search, delivery, from = from?.ToString("yyyy-MM-dd"), to = to?.ToString("yyyy-MM-dd") });
    }
    private bool TryOwner(out Guid owner) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out owner);
}
