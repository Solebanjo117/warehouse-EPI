using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Web.Localization;
using WarehouseEPI.Web.Pages.Operations.CycleCounts;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.CycleCountPlans;

[Authorize(Policy = "AdminOnly")]
public sealed class EditModel(CycleCountService cycleCounts, IStringLocalizer<CatalogTexts> text) : PageModel
{
    public CycleCountPlanCatalogItem? Plan { get; private set; }
    public IReadOnlyList<SelectListItem> FrequencyOptions { get; } = Enum.GetValues<CycleCountFrequency>()
        .Select(value => new SelectListItem(CycleCountPlanPresentation.FrequencyLabel(value), value.ToString()))
        .ToArray();

    [BindProperty] public InputModel Input { get; set; } = new();
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Plan = await cycleCounts.GetPlanAsync(id, cancellationToken);
        if (Plan is null) return NotFound();
        Input = new() { Id = Plan.Id, Frequency = Plan.Frequency, AnchorDate = Plan.AnchorDate };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(Input.Frequency)) ModelState.AddModelError("Input.Frequency", text["Selecciona una frecuencia válida."].Value);
        if (Input.AnchorDate == default) ModelState.AddModelError("Input.AnchorDate", text["La fecha inicial es obligatoria."].Value);
        Plan = await cycleCounts.GetPlanAsync(Input.Id, cancellationToken);
        if (Plan is null) return NotFound();
        if (!ModelState.IsValid) return Page();

        var result = await cycleCounts.UpdatePlanAsync(new(Input.Id, Input.Frequency, Input.AnchorDate, CurrentUserId()), cancellationToken);
        if (result.Status == CycleCountStatus.Success) return RedirectToPage("Index");
        Error = string.Join(' ', result.ValidationErrors);
        return Page();
    }

    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    public sealed class InputModel
    {
        public Guid Id { get; set; }
        public CycleCountFrequency Frequency { get; set; }
        public DateOnly AnchorDate { get; set; }
    }
}
