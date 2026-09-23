using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using System.Security.Claims;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Pages.Operations.CycleCounts;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.CycleCountPlans;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(
    CycleCountService cycleCounts,
    OperationalInventoryQueryService operationalQuery,
    WarehouseClock warehouseClock,
    TimeProvider timeProvider,
    IStringLocalizer<CatalogTexts> text) : PageModel
{
    public PagedResult<CycleCountPlanCatalogItem> Result { get; private set; } = new([], 0, 1, 25);
    public IReadOnlyList<CycleCountPlanCatalogItem> Plans => Result.Items;
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string Status { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public IReadOnlyList<SelectListItem> FrequencyOptions { get; } = Enum.GetValues<CycleCountFrequency>()
        .Select(value => new SelectListItem(CycleCountPlanPresentation.FrequencyLabel(value), value.ToString()))
        .ToArray();
    public OperationalProductResult? SelectedProduct { get; private set; }
    public OperationalLocationResult? SelectedLocation { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? ProductSearch { get; set; }

    [BindProperty]
    public string? LocationSearch { get; set; }

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Input.AnchorDate = await warehouseClock.GetDateAsync(timeProvider.GetUtcNow(), cancellationToken);
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        ValidateInput();
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var result = await cycleCounts.CreatePlanAsync(
            new(Input.ProductId, Input.LocationId, Input.Frequency, Input.AnchorDate, CurrentUserId()), cancellationToken);
        if (result.Status == CycleCountStatus.Success)
            return RedirectToPage(new { search = Search, status = Status, pageNumber = PageNumber });

        Error = string.Join(' ', result.ValidationErrors);
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, bool active, CancellationToken cancellationToken)
    {
        var current = await cycleCounts.GetPlanAsync(id, cancellationToken);
        if (current is null)
            return NotFound();

        var result = await cycleCounts.SetPlanActiveAsync(new(id, active, CurrentUserId()), cancellationToken);
        if (result.Status == CycleCountStatus.Success)
            return RedirectToPage(new { search = Search, status = Status, pageNumber = PageNumber });

        Error = string.Join(' ', result.ValidationErrors);
        await LoadAsync(cancellationToken);
        return Page();
    }

    private void ValidateInput()
    {
        if (Input.ProductId == Guid.Empty)
            ModelState.AddModelError("Input.ProductId", text["Selecciona un SKU de los resultados."].Value);
        if (Input.LocationId == Guid.Empty)
            ModelState.AddModelError("Input.LocationId", text["Selecciona una ubicación de los resultados."].Value);
        if (!Enum.IsDefined(Input.Frequency))
            ModelState.AddModelError("Input.Frequency", text["Selecciona una frecuencia válida."].Value);
        if (Input.AnchorDate == default)
            ModelState.AddModelError("Input.AnchorDate", text["La fecha inicial es obligatoria."].Value);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        bool? active = Status switch { "active" => true, "paused" => false, _ => null };
        Result = await cycleCounts.GetPlanCatalogAsync(new(Search, active, PageNumber, 25), cancellationToken);
        SelectedProduct = Input.ProductId == Guid.Empty
            ? null
            : await operationalQuery.GetProductAsync(Input.ProductId, cancellationToken: cancellationToken);
        SelectedLocation = Input.LocationId == Guid.Empty
            ? null
            : await operationalQuery.GetLocationAsync(Input.LocationId, cancellationToken: cancellationToken);

        if (SelectedProduct is not null)
            ProductSearch = SelectedProduct.Sku;
        if (SelectedLocation is not null)
            LocationSearch = SelectedLocation.Code;
    }

    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id
        : Guid.Empty;

    public sealed class InputModel
    {
        public Guid ProductId { get; set; }
        public Guid LocationId { get; set; }
        public CycleCountFrequency Frequency { get; set; } = CycleCountFrequency.Monthly;
        public DateOnly AnchorDate { get; set; }
    }
}
