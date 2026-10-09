using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Operations.PalletLabels;

public sealed class IndexModel(LabelTemplateService templates, LabelDocumentService documents,
    PalletLicensePlateService plates, WarehouseClock clock, PalletTrackingService tracking,
    OperationalInventoryQueryService operationalQuery, InventoryHistoryService inventoryHistory,
    IStringLocalizer<OperationsTexts> texts, StagingArrivalQuery stagingArrivals) : PageModel
{
    [BindProperty] public IdentificationInput Identify { get; set; } = new();
    [BindProperty] public ConsolidationInput Consolidate { get; set; } = new();
    [BindProperty] public MovementPrintInput MovementPrint { get; set; } = new();
    [BindProperty] public PrintInput Input { get; set; } = new();
    [BindProperty] public StagingInput Staging { get; set; } = new();
    public bool IsStaging { get; private set; }
    public StagingArrivalRow? SelectedArrival { get; private set; }
    public int ArrivalBatch { get; private set; }
    public int ArrivalBatchCount { get; private set; }
    public Guid? SelectedStagingEntryId { get; private set; }
    public StagingEntryPage StagingEntries { get; private set; } = new([], false);
    public Dictionary<Guid, DateTimeOffset> StagingTimes { get; } = [];
    [BindProperty(SupportsGet = true)] public int EntryPage { get; set; } = 1;
    public List<LabelRenderDocument> BatchPreviews { get; } = [];
    public PalletLicensePlateEntry? Entry { get; private set; }
    public DateTimeOffset? EntryLocalTime { get; private set; }
    public LabelRenderDocument? Preview { get; private set; }
    public IReadOnlyList<string> PrintWarnings { get; private set; } = [];
    public string? SearchError { get; private set; }
    public OperationalLocationResult? SelectedLocation { get; private set; }
    public OperationalProductResult? SelectedProduct { get; private set; }
    public IReadOnlyList<PalletIdentificationProduct> Products { get; private set; } = [];
    public IReadOnlyList<PalletStockLocation> ProductLocations { get; private set; } = [];
    public IReadOnlyDictionary<Guid, IReadOnlyList<PalletQueryRow>> PrintablePlates { get; private set; }
        = new Dictionary<Guid, IReadOnlyList<PalletQueryRow>>();
    public IReadOnlyList<RecentMovement> RecentMovements { get; private set; } = [];

    public async Task OnGetAsync(string? location, string? product, Guid? productId, string? folio, Guid? id, bool print = false, CancellationToken token = default)
    {
        Guid? suggestedProduct = productId;
        var printSuggestedPlate = false;
        var currentPlate = id.HasValue ? await plates.LoadAsync(id.Value, token) : null;
        // A plate can keep its original STAGING entry ID after being transferred.
        // Only apply STAGING's entry-print rules while the plate is still there.
        if (id.HasValue && !(currentPlate?.Entry is { IsTracked: true, Destination: not "STAGING" }) &&
            await tracking.IsStagingEntryAsync(id.Value, token))
        {
            var suggestion = await tracking.SuggestionAsync(id.Value, token);
            if (suggestion is not null && !await tracking.CanPrintStagingPlatesAsync(suggestion.LocationId, [id.Value], token))
            {
                location = "STAGING";
                suggestedProduct = suggestion.ProductId;
                id = null;
            }
        }
        if (id.HasValue)
        {
            var load = currentPlate!;
            if (load.Status == PalletLicensePlateStatus.Success && load.Entry!.IsTracked)
            {
                await LoadEntryAsync(id.Value, token);
                if (print) await BuildPreviewAsync(token);
            }
            else
            {
                var suggestion = await tracking.SuggestionAsync(id.Value, token);
                if (suggestion is not null)
                {
                    location = suggestion.LocationCode;
                    suggestedProduct = suggestion.ProductId;
                    printSuggestedPlate = print;
                }
                else SearchError = texts["Ese movimiento no tiene una placa o una combinación de producto y ubicación que se pueda imprimir."];
            }
        }
        else if (!string.IsNullOrWhiteSpace(folio))
        {
            if (!PalletLicensePlateService.TryParseFolio(folio, out var plateId)) SearchError = texts["Escribe un UUID de Entrada o un folio PLT válido."];
            else { await LoadEntryAsync(plateId, token); if (Entry is not null) await BuildPreviewAsync(token); }
        }
        await LoadIdentificationAsync(location, product, suggestedProduct, token);
        var suggestedSummary = suggestedProduct.HasValue ? Products.FirstOrDefault(x => x.ProductId == suggestedProduct.Value) : null;
        if (printSuggestedPlate && suggestedProduct.HasValue && suggestedSummary?.Identifiable <= 0 &&
            PrintablePlates.TryGetValue(suggestedProduct.Value, out var suggestedPlates) && suggestedPlates.Count == 1)
        {
            await LoadEntryAsync(suggestedPlates[0].Id, token);
            await BuildPreviewAsync(token);
        }
    }

    public async Task<IActionResult> OnGetProductOptionsAsync(string? q, Guid? locationId, CancellationToken token) =>
        new JsonResult(await tracking.StockProductsAsync(locationId, q, token));

    public async Task<IActionResult> OnGetStagingEntryAsync(Guid movementId, CancellationToken token)
    {
        SelectedStagingEntryId = movementId;
        await LoadIdentificationAsync("STAGING", null, null, token);
        var row = StagingEntries.Items.SingleOrDefault();
        if (row is null)
            ModelState.AddModelError(string.Empty, texts["La entrada no está vigente o no pertenece a STAGING."]);
        else if (!row.NeedsIdentification && row.Plates.Count > 0)
        {
            Staging.LocationId = SelectedLocation!.Id;
            Staging.PlateIds = row.Plates.Select(x => x.Id).ToList();
            await BuildStagingPreviewAsync(token);
        }
        return Page();
    }

    public async Task<IActionResult> OnGetStagingArrivalAsync(Guid arrivalLineId, CancellationToken token, int arrivalBatch = 1)
    {
        SelectedArrival = await stagingArrivals.GetAsync(arrivalLineId, token);
        if (SelectedArrival is null) return NotFound();
        if (!SelectedArrival.NeedsIdentification && SelectedArrival.Pending > 0)
        {
            Staging.LocationId = SelectedArrival.LocationId;
            var selection = StagingArrivalQuery.DecodeVersion(SelectedArrival.Version)!;
            ArrivalBatchCount = (selection.Count + 99) / 100;
            ArrivalBatch = Math.Clamp(arrivalBatch, 1, ArrivalBatchCount);
            Staging.PlateIds = selection.Skip((ArrivalBatch - 1) * 100).Take(100).Select(p => p.PlateId).ToList();
            await BuildStagingPreviewAsync(token, SelectedArrival);
        }
        return Page();
    }

    public async Task<IActionResult> OnGetLocationOptionsAsync(string? q, Guid? productId, CancellationToken token) =>
        new JsonResult(await tracking.StockLocationsAsync(productId, q, token));

    public async Task<IActionResult> OnPostIdentifyAsync(CancellationToken token)
    {
        RetainModelStateFor(nameof(Identify));
        if (!ModelState.IsValid) { await ReloadIdentificationAsync(token); return Page(); }
        var result = await tracking.IdentifyAsync(new(Identify.OperationId, Identify.LocationId, Identify.ProductId,
            Identify.Quantity, Identify.ExpectedBalanceVersion, Identify.ExpectedIdentifiable), token);
        if (result.Status == InventoryMovementStatus.Success && result.Plates?.FirstOrDefault() is { } plate)
            return RedirectToPage(pageName: null, pageHandler: null,
                routeValues: new { id = plate.PlateId, print = true }, fragment: "label-preview");
        foreach (var error in result.ValidationErrors.DefaultIfEmpty(result.Status switch
        {
            InventoryMovementStatus.IdempotencyConflict => "La operación ya fue usada con datos distintos.",
            InventoryMovementStatus.BalanceChanged => "El saldo cambió. Consulta nuevamente antes de identificar el pallet.",
            _ => "No fue posible identificar el pallet."
        })) ModelState.AddModelError(string.Empty, texts[error]);
        await ReloadIdentificationAsync(token);
        return Page();
    }

    public async Task<IActionResult> OnPostPreparePrintAsync(CancellationToken token)
    {
        RetainModelStateFor(nameof(Consolidate));
        if (!ModelState.IsValid) { await ReloadIdentificationAsync(token); return Page(); }
        var result = await tracking.ConsolidateAsync(new(Consolidate.OperationId, Consolidate.ProductId, Consolidate.LocationId), token);
        if (result.Status == InventoryMovementStatus.Success && result.Plates?.SingleOrDefault() is { } plate)
            return RedirectToPage(pageName: null, pageHandler: null,
                routeValues: new { id = plate.PlateId, print = true }, fragment: "label-preview");
        foreach (var error in result.ValidationErrors.DefaultIfEmpty(result.Status == InventoryMovementStatus.IdempotencyConflict
                     ? "La operación ya fue usada con datos distintos."
                     : "No fue posible preparar la placa para imprimir."))
            ModelState.AddModelError(string.Empty, texts[error]);
        Identify.LocationId = Consolidate.LocationId;
        Identify.ProductId = Consolidate.ProductId;
        await ReloadIdentificationAsync(token);
        return Page();
    }

    public async Task<IActionResult> OnPostPrepareMovementPrintAsync(CancellationToken token)
    {
        RetainModelStateFor(nameof(MovementPrint));
        if (MovementPrint.OperationId == Guid.Empty || MovementPrint.MovementId == Guid.Empty)
            ModelState.AddModelError(string.Empty, texts["Selecciona un movimiento válido para imprimir."]);
        if (!ModelState.IsValid)
        {
            await LoadIdentificationAsync(null, null, null, token);
            return Page();
        }
        var suggestion = await tracking.SuggestionAsync(MovementPrint.MovementId, token);
        if (suggestion is null)
        {
            ModelState.AddModelError(string.Empty, texts["Ese movimiento no tiene una placa o una combinación de producto y ubicación que se pueda imprimir."]);
            await LoadIdentificationAsync(null, null, null, token);
            return Page();
        }
        if (await tracking.IsStagingAsync(suggestion.LocationId, token))
            return RedirectToPage(new { location = suggestion.LocationCode, productId = suggestion.ProductId });
        var summary = (await tracking.IdentificationProductsAsync(suggestion.LocationId, token))
            .SingleOrDefault(x => x.ProductId == suggestion.ProductId);
        if (summary is null)
        {
            ModelState.AddModelError(string.Empty, texts["El producto ya no tiene stock positivo en la ubicación del movimiento."]);
            await LoadIdentificationAsync(suggestion.LocationCode, null, suggestion.ProductId, token);
            return Page();
        }
        var result = summary.Identifiable > 0
            ? await tracking.IdentifyAsync(new(MovementPrint.OperationId, suggestion.LocationId, suggestion.ProductId,
                summary.Identifiable, summary.BalanceVersion, summary.Identifiable), token)
            : await tracking.ConsolidateAsync(new(MovementPrint.OperationId, suggestion.ProductId, suggestion.LocationId), token);
        if (result.Status == InventoryMovementStatus.Success && result.Plates?.FirstOrDefault() is { } plate)
            return RedirectToPage(pageName: null, pageHandler: null,
                routeValues: new { id = plate.PlateId, print = true }, fragment: "label-preview");
        foreach (var error in result.ValidationErrors.DefaultIfEmpty(result.Status == InventoryMovementStatus.IdempotencyConflict
                     ? "La operación ya fue usada con datos distintos."
                     : "No fue posible preparar la placa para imprimir."))
            ModelState.AddModelError(string.Empty, texts[error]);
        await LoadIdentificationAsync(suggestion.LocationCode, null, suggestion.ProductId, token);
        return Page();
    }

    public async Task<IActionResult> OnPostPrintAsync(CancellationToken token)
    {
        RetainModelStateFor(nameof(Input));
        if (Input.MovementId == Guid.Empty) ModelState.AddModelError("Input.MovementId", texts["Busca una placa confirmada."]);
        if (Input.Weight?.Length > 40 || ContainsUnsafeText(Input.Weight)) ModelState.AddModelError("Input.Weight", texts["El peso debe tener hasta 40 caracteres imprimibles."]);
        await LoadEntryAsync(Input.MovementId, token);
        await LoadIdentificationAsync(null, null, null, token);
        if (!ModelState.IsValid || Entry is null) return Page();
        await BuildPreviewAsync(token);
        return Page();
    }

    private void RetainModelStateFor(string propertyName)
    {
        var prefix = propertyName + ".";
        foreach (var key in ModelState.Keys.Where(key =>
                     !key.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                     !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            ModelState.Remove(key);
    }

    private async Task LoadIdentificationAsync(string? locationCode, string? productCode, Guid? productId, CancellationToken token)
    {
        await LoadRecentMovementsAsync(token);
        if (!string.IsNullOrWhiteSpace(productCode)) SelectedProduct = await operationalQuery.ResolveProductAsync(productCode, cancellationToken: token);
        else if (productId.HasValue) SelectedProduct = await operationalQuery.GetProductAsync(productId.Value, cancellationToken: token);
        if (!string.IsNullOrWhiteSpace(productCode) && SelectedProduct is null)
            SearchError ??= texts["No existe un producto activo con ese código."];
        if (SelectedProduct is not null)
        {
            Identify.ProductId = SelectedProduct.Id;
            ProductLocations = await tracking.StockLocationsAsync(SelectedProduct.Id, token: token);
        }
        if (!string.IsNullOrWhiteSpace(locationCode))
        {
            SelectedLocation = await operationalQuery.ResolveLocationAsync(locationCode, cancellationToken: token);
            if (SelectedLocation is null) { SearchError ??= texts["No existe una ubicación operativa con ese código."]; return; }
            Identify.LocationId = SelectedLocation.Id;
            IsStaging = await tracking.IsStagingAsync(SelectedLocation.Id, token);
            if (IsStaging)
            {
                EntryPage = Math.Clamp(EntryPage, 1, 100000);
                StagingEntries = await tracking.StagingEntriesAsync(SelectedLocation.Id, SelectedProduct?.Id, EntryPage, SelectedStagingEntryId, token);
                foreach (var row in StagingEntries.Items) StagingTimes[row.MovementId] = await clock.ConvertAsync(row.OccurredAt, token);
            }
            Products = await tracking.IdentificationProductsAsync(SelectedLocation.Id, token);
            await LoadPrintablePlatesAsync(SelectedLocation.Id, token);
        }
    }

    private async Task ReloadIdentificationAsync(CancellationToken token)
    {
        await LoadRecentMovementsAsync(token);
        SelectedLocation = await operationalQuery.GetLocationAsync(Identify.LocationId, cancellationToken: token);
        if (SelectedLocation is null) return;
        Products = await tracking.IdentificationProductsAsync(Identify.LocationId, token);
        await LoadPrintablePlatesAsync(Identify.LocationId, token);
        var productId = Identify.ProductId != Guid.Empty ? Identify.ProductId : Consolidate.ProductId;
        if (productId != Guid.Empty)
        {
            SelectedProduct = await operationalQuery.GetProductAsync(productId, cancellationToken: token);
            ProductLocations = await tracking.StockLocationsAsync(productId, token: token);
        }
    }

    private async Task LoadPrintablePlatesAsync(Guid locationId, CancellationToken token)
    {
        var plates = new Dictionary<Guid, IReadOnlyList<PalletQueryRow>>();
        foreach (var product in Products)
        {
            var availability = await tracking.AvailableAsync(product.ProductId, locationId, token: token);
            var active = availability.Plates.Where(x => x.Quantity > 0).ToArray();
            if (active.Length > 0) plates[product.ProductId] = active;
        }
        PrintablePlates = plates;
    }

    private async Task LoadEntryAsync(Guid id, CancellationToken token)
    {
        if (id == Guid.Empty) return;
        var result = await plates.LoadAsync(id, token);
        if (result.Status == PalletLicensePlateStatus.Success)
        {
            Entry = result.Entry; Input.MovementId = id;
            EntryLocalTime = await clock.ConvertAsync(result.Entry!.OccurredAt, token);
        }
        else SearchError = result.Error;
    }

    private async Task BuildPreviewAsync(CancellationToken token)
    {
        if (Entry is null) return;
        var choice = await templates.GetPublishedByCodeAsync("PLT-LICENSE-PLATE", LabelTemplateKind.PalletLicensePlate, token);
        var version = choice is null ? null : await templates.GetPublishedEntityAsync(choice.VersionId, token);
        if (version is null) { ModelState.AddModelError(string.Empty, texts["La plantilla de placa no está publicada."]); return; }
        var localDate = (await clock.ConvertAsync(Entry.OccurredAt, token)).Date;
        var result = documents.Render(version, PalletLicensePlateService.Product(Entry),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["weight"] = Input.Weight?.Trim() ?? string.Empty },
            Input.Copies, PalletLicensePlateService.SystemValues(Entry, DateOnly.FromDateTime(localDate)));
        foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error);
        PrintWarnings = result.Warnings; Preview = result.Document;
    }

    private static bool ContainsUnsafeText(string? value) => value?.Any(character => char.IsControl(character) || character is '<' or '>') == true;

    private async Task LoadRecentMovementsAsync(CancellationToken token)
    {
        var page = await inventoryHistory.SearchAsync(new(null, null, null, null, null, null, null), 1, 10, token);
        var printable = await tracking.PrintablePlatesForMovementsAsync(page.Items.Select(x => x.Id).ToArray(), token);
        var rows = new List<RecentMovement>(page.Items.Count);
        foreach (var item in page.Items)
            rows.Add(new(item, await clock.ConvertAsync(item.OccurredAt, token),
                printable.GetValueOrDefault(item.Id) ?? [], await tracking.IsStagingEntryAsync(item.Id, token)));
        RecentMovements = rows;
    }

    public async Task<IActionResult> OnPostIdentifyStagingEntryAsync(CancellationToken token)
    {
        RetainModelStateFor(nameof(Staging));
        if (ModelState.IsValid)
        {
            var entries = await tracking.StagingEntriesAsync(Staging.LocationId,
                movementId: Staging.MovementId, token: token);
            var quantity = entries.Items.SingleOrDefault()?.Received ?? 0;
            var result = await tracking.IdentifyStagingEntryAsync(new(Staging.OperationId, Staging.MovementId,
                Staging.LocationId, quantity), token);
            if (result.Status == InventoryMovementStatus.Success && result.Plates?.SingleOrDefault() is { } plate)
                return RedirectToPage(pageName: null, pageHandler: null,
                    routeValues: new { id = plate.PlateId, location = "STAGING", print = true }, fragment: "label-preview");
            foreach (var error in result.ValidationErrors.DefaultIfEmpty("No fue posible identificar la entrada. Consulta nuevamente."))
                ModelState.AddModelError(string.Empty, texts[error]);
        }
        await LoadIdentificationAsync("STAGING", null, null, token);
        return Page();
    }

    public async Task<IActionResult> OnPostPrintStagingSelectionAsync(CancellationToken token)
    {
        RetainModelStateFor(nameof(Staging));
        await BuildStagingPreviewAsync(token);
        await LoadIdentificationAsync("STAGING", null, null, token);
        return Page();
    }

    private async Task BuildStagingPreviewAsync(CancellationToken token, StagingArrivalRow? arrival = null)
    {
        var allowed = arrival is null
            ? await tracking.CanPrintStagingPlatesAsync(Staging.LocationId, Staging.PlateIds, token)
            : await tracking.CanPrintStagingArrivalAsync(arrival.LineId, Staging.LocationId, Staging.PlateIds, arrival.Version, token);
        if (!allowed)
            ModelState.AddModelError(string.Empty, texts["Selecciona placas vigentes de entradas en STAGING. Consulta nuevamente si cambiaron."]);
        if (ModelState.IsValid)
        {
            foreach (var id in Staging.PlateIds.Distinct())
            {
                Entry = null;
                Preview = null;
                await LoadEntryAsync(id, token);
                if (Entry is null)
                {
                    ModelState.AddModelError(string.Empty, texts["Selecciona placas vigentes de entradas en STAGING. Consulta nuevamente si cambiaron."]);
                    break;
                }
                await BuildPreviewAsync(token);
                if (Preview is not null) BatchPreviews.Add(Preview);
            }
            if (arrival is not null || Staging.PlateIds.Distinct().Count() > 1) Entry = null;
            if (arrival is not null && !await tracking.CanPrintStagingArrivalAsync(arrival.LineId,
                Staging.LocationId, Staging.PlateIds, arrival.Version, token))
                ModelState.AddModelError(string.Empty, texts["Selecciona placas vigentes de entradas en STAGING. Consulta nuevamente si cambiaron."]);
            if (!ModelState.IsValid) { Preview = null; BatchPreviews.Clear(); }
        }
    }

    public sealed class StagingInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        public Guid MovementId { get; set; }
        public Guid LocationId { get; set; }
        public List<Guid> PlateIds { get; set; } = [];
    }

    public sealed class IdentificationInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        [Required] public Guid LocationId { get; set; }
        [Required] public Guid ProductId { get; set; }
        [Range(typeof(decimal), "0.0001", "99999999999999.9999")] public decimal Quantity { get; set; }
        public uint ExpectedBalanceVersion { get; set; }
        public decimal ExpectedIdentifiable { get; set; }
    }
    public sealed class ConsolidationInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        [Required] public Guid LocationId { get; set; }
        [Required] public Guid ProductId { get; set; }
    }
    public sealed class MovementPrintInput
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        [Required] public Guid MovementId { get; set; }
    }
    public sealed record RecentMovement(InventoryMovementHistoryRow Movement, DateTimeOffset LocalOccurredAt,
        IReadOnlyList<PalletQueryRow> PrintablePlates, bool IsStagingEntry = false);
    public sealed class PrintInput
    {
        public Guid MovementId { get; set; }
        [StringLength(40)] public string? Weight { get; set; }
        [Range(1, 100)] public int Copies { get; set; } = 1;
    }
}
