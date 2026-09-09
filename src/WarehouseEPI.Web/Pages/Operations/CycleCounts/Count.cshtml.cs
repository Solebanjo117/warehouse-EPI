using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Pages.Operations.CycleCounts;

public sealed class CountModel(CycleCountService cycleCountService, WarehouseDbContext dbContext,
    WarehouseEPI.Web.Security.CycleCountPreparationProtector preparationProtector,
    WarehouseEPI.Web.Security.CycleCountOperatorSession operatorSessions) : PageModel
{
    public CycleCountAttemptView? Attempt { get; private set; }
    [BindProperty] public InputModel Input { get; set; } = new();
    public Guid CampaignId { get; private set; }
    public Guid LocationId { get; private set; }
    public string? Error { get; private set; }
    public string? PreparationToken { get; private set; }
    public WarehouseEPI.Web.Security.CycleCountOperatorSessionView? OperatorSession { get; private set; }
    public bool RequireOperatorPin { get; private set; }
    public HashSet<Guid> MissingQuantityProductIds { get; } = [];

    public decimal? CapturedQuantity(Guid productId) =>
        Input.Entries.FirstOrDefault(item => item.ProductId == productId)?.Quantity;

    public UnexpectedEntryInput UnexpectedEntry(int index) =>
        index < Input.UnexpectedEntries.Count ? Input.UnexpectedEntries[index] : new();

    public async Task<IActionResult> OnGetAsync(Guid id, Guid locationId, Guid? attemptId, CancellationToken cancellationToken)
    {
        CampaignId = id; LocationId = locationId;
        OperatorSession = await operatorSessions.GetAsync(HttpContext, id, renew: true, cancellationToken);
        RequireOperatorPin = OperatorSession is null;
        Response.Headers.CacheControl = "no-store";
        if (attemptId is Guid legacyAttempt)
        {
            var campaign = await cycleCountService.GetCampaignAsync(id, cancellationToken);
            if (campaign?.Locations.SingleOrDefault(item => item.Id == locationId)?.ActiveAttemptId != legacyAttempt) return NotFound();
            Attempt = await cycleCountService.GetAttemptAsync(legacyAttempt, false, cancellationToken);
            if (Attempt is null) return NotFound();
            Input.AttemptId = legacyAttempt;
            return Page();
        }
        var preparation = await cycleCountService.PrepareAsync(id, locationId, cancellationToken);
        if (preparation is null) return NotFound();
        PreparationToken = preparationProtector.Protect(preparation);
        Attempt = BlindView(preparation);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, Guid locationId, CancellationToken cancellationToken)
    {
        CampaignId = id; LocationId = locationId;
        var pin = Input.Pin;
        Input.Pin = string.Empty;
        Response.Headers.CacheControl = "no-store";
        CycleCountPreparation? preparation = null;
        if (Input.AttemptId is Guid legacyAttempt)
        {
            Attempt = await cycleCountService.GetAttemptAsync(legacyAttempt, false, cancellationToken);
            if (Attempt is null) { Error = "El intento abierto ya no está disponible. Vuelve a la campaña."; return Page(); }
        }
        else
        {
            if (!preparationProtector.TryUnprotect(Input.PreparationToken, out preparation) || preparation is null || preparation.CampaignId != id || preparation.CycleCountLocationId != locationId)
            { Error = "La preparación expiró o no es válida. Vuelve a escanear la ubicación."; return Page(); }
            PreparationToken = Input.PreparationToken;
            Attempt = BlindView(preparation);
        }

        // Una cantidad vacía o ilegible nunca debe convertirse en cero: se identifica la
        // línea, se conserva lo capturado y se pide corregirla.
        var entries = new List<CycleCountQuantityCommand>();
        var missing = new List<string>();
        foreach (var item in Input.Entries)
        {
            if (item.Quantity is decimal quantity) { entries.Add(new(item.ProductId, quantity)); continue; }
            MissingQuantityProductIds.Add(item.ProductId);
            missing.Add(Attempt.Entries.FirstOrDefault(entry => entry.ProductId == item.ProductId)?.Sku ?? "el producto de la lista");
        }
        if (!Input.IsLocationEmpty && missing.Count != 0)
        {
            Error = $"Captura la cantidad física de {string.Join(", ", missing)}, incluso si es cero. Marca la ubicación como vacía si no hay existencias.";
            return Page();
        }
        if (!ModelState.IsValid)
        {
            Error = "Revisa la captura: hay valores que el sistema no pudo interpretar.";
            return Page();
        }

        foreach (var item in Input.UnexpectedEntries.Where(item => !string.IsNullOrWhiteSpace(item.Code)))
        {
            var code = item.Code!.Trim();
            if (item.Quantity is null)
            {
                Error = $"Captura la cantidad física del producto inesperado {code}, incluso si es cero.";
                return Page();
            }
            var normalized = code.ToUpperInvariant();
            var productId = await dbContext.Products.AsNoTracking()
                .Where(product => product.Sku.ToUpper() == normalized || product.Barcodes.Any(barcode => barcode.IsActive && barcode.Barcode == code))
                .Select(product => (Guid?)product.Id).SingleOrDefaultAsync(cancellationToken);
            if (productId is null)
            {
                Error = $"No se encontró el producto inesperado {code}.";
                return Page();
            }
            entries.Add(new(productId.Value, item.Quantity.Value));
        }

        OperatorSession = await operatorSessions.GetAsync(HttpContext, id, renew: true, cancellationToken);
        if (OperatorSession is null && !string.IsNullOrWhiteSpace(pin))
            OperatorSession = await operatorSessions.StartAsync(HttpContext, id, pin, cancellationToken);
        if (OperatorSession is null)
        {
            RequireOperatorPin = true;
            Error = string.IsNullOrWhiteSpace(pin)
                ? "Tu sesión de conteo terminó. Ingresa tu NIP para conservar la captura y continuar."
                : "No fue posible validar el NIP. La captura permanece disponible para reintentar.";
            return Page();
        }

        var result = Input.AttemptId is Guid attemptId
            ? await cycleCountService.SubmitForUserAsync(new(attemptId, Input.OperationId, OperatorSession.UserId, entries, Input.IsLocationEmpty), cancellationToken)
            : await cycleCountService.SubmitPreparedForUserAsync(new(preparation!, Input.OperationId, OperatorSession.UserId, entries, Input.IsLocationEmpty), cancellationToken);
        if (result.Status == CycleCountStatus.Success) return RedirectToPage("Details", new { id = result.CampaignId ?? id, submittedLocationId = result.LocationId ?? locationId, scanNext = true });
        if (result.Status == CycleCountStatus.InvalidPin)
        {
            operatorSessions.Clear(HttpContext);
            OperatorSession = null;
            RequireOperatorPin = true;
        }
        Error = CycleCountPresentation.StatusMessage(result);
        return Page();
    }

    private static CycleCountAttemptView BlindView(CycleCountPreparation preparation) => new(
        Guid.Empty, 1, WarehouseEPI.Core.Entities.CycleCountAttemptStatus.Counting, preparation.PreparedAt, string.Empty, null, null,
        preparation.Entries.Select(item => new CycleCountEntryItem(item.ProductId, item.Sku, item.Description, item.UnitCode, item.AllowsDecimals, null, null, null, false)).ToArray());

    public sealed class InputModel { public string? PreparationToken { get; set; } public Guid? AttemptId { get; set; } public Guid OperationId { get; set; } public string Pin { get; set; } = string.Empty; public bool IsLocationEmpty { get; set; } public List<EntryInput> Entries { get; set; } = []; public List<UnexpectedEntryInput> UnexpectedEntries { get; set; } = []; }
    public sealed class EntryInput { public Guid ProductId { get; set; } public decimal? Quantity { get; set; } }
    public sealed class UnexpectedEntryInput { public string? Code { get; set; } public decimal? Quantity { get; set; } }
}
