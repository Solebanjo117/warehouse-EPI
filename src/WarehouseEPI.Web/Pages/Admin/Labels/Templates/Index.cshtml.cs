using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Labels;

namespace WarehouseEPI.Web.Pages.Admin.Labels.Templates;

public sealed class IndexModel(LabelTemplateService templates) : PageModel
{
    public IReadOnlyList<LabelTemplateAdminRow> Rows { get; private set; } = [];
    public IReadOnlyList<LabelSizeDefinition> Sizes => LabelSizeRegistry.All;

    [BindProperty] public CreateInput Create { get; set; } = new();
    [BindProperty] public string RetireReason { get; set; } = string.Empty;
    [BindProperty] public string RetirePin { get; set; } = string.Empty;

    public sealed class CreateInput
    {
        [Required, StringLength(60)] public string Code { get; set; } = string.Empty;
        [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
        public LabelSizePreset Size { get; set; } = LabelSizePreset.SixByFourLandscape;
    }

    public async Task OnGetAsync(CancellationToken token) => await LoadAsync(token);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken token)
    {
        if (!ModelState.IsValid) { await LoadAsync(token); return Page(); }
        var result = await templates.CreateAsync(CurrentUserId(), Create.Code, Create.Name, Create.Size, token);
        return await CompleteAsync(result, "Plantilla creada como borrador.", token);
    }

    public async Task<IActionResult> OnPostDuplicateAsync(Guid id, CancellationToken token) =>
        await CompleteAsync(await templates.DuplicateAsync(CurrentUserId(), id, token), "Se creó la siguiente versión como borrador.", token);

    public async Task<IActionResult> OnPostRetireAsync(Guid id, CancellationToken token)
    {
        var result = await templates.RetireAsync(CurrentUserId(), id, RetirePin, RetireReason, token);
        RetirePin = string.Empty;
        ModelState.Remove(nameof(RetirePin));
        return await CompleteAsync(result, "La versión vigente fue retirada.", token);
    }

    private async Task<IActionResult> CompleteAsync(LabelTemplateMutationResult result, string success, CancellationToken token)
    {
        if (result.Status == LabelTemplateMutationStatus.Success)
        {
            TempData["StatusMessage"] = success;
            return result.VersionId is { } id ? RedirectToPage("Edit", new { id }) : RedirectToPage();
        }
        ModelState.AddModelError(string.Empty, result.Status == LabelTemplateMutationStatus.InvalidPin
            ? "El NIP debe pertenecer a un ADMIN activo."
            : string.Join(" ", result.Errors ?? ["No fue posible completar la operación."]));
        await LoadAsync(token);
        return Page();
    }

    private async Task LoadAsync(CancellationToken token) => Rows = await templates.GetAdminRowsAsync(token);
    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
