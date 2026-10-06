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
    IStringLocalizer<OperationsTexts> texts) : PageModel
{
    [BindProperty] public IdentificationInput Identify { get; set; } = new();
    [BindProperty] public ConsolidationInput Consolidate { get; set; } = new();
    [BindProperty] public MovementPrintInput MovementPrint { get; set; } = new();
    [BindProperty] public PrintInput Input { get; set; } = new();
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
        if (id.HasValue)
        {
            var load = await plates.LoadAsync(id.Value, token);
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
                printable.GetValueOrDefault(item.Id) ?? []));
        RecentMovements = rows;
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
        IReadOnlyList<PalletQueryRow> PrintablePlates);
    public sealed class PrintInput
    {
        public Guid MovementId { get; set; }
        [StringLength(40)] public string? Weight { get; set; }
        [Range(1, 100)] public int Copies { get; set; } = 1;
    }
}
