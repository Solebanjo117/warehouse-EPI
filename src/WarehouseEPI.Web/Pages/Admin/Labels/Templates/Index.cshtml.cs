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
    public IReadOnlyList<TemplateGroup> Groups { get; private set; } = [];
    public IReadOnlyList<LabelSizeDefinition> Sizes => LabelSizeRegistry.All;

    /// <summary>
    /// Una plantilla vista como la administra el servicio: la versión que imprime
    /// hoy, la única editable que puede existir y el resto como historial.
    /// </summary>
    public sealed record TemplateGroup(
        Guid TemplateId,
        string Code,
        string Name,
        LabelTemplateKind Kind,
        LabelTemplateAdminRow? Current,
        LabelTemplateAdminRow? Editable,
        IReadOnlyList<LabelTemplateAdminRow> History);

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

    private async Task LoadAsync(CancellationToken token)
    {
        Rows = await templates.GetAdminRowsAsync(token);
        Groups = [.. Rows
            .GroupBy(row => row.TemplateId)
            .Select(group =>
            {
                var versions = group.OrderByDescending(row => row.Version).ToList();
                var current = versions.Find(row => row.IsCurrent);
                var editable = versions.Find(row => row.Status is LabelTemplateStatus.Draft or LabelTemplateStatus.InValidation);
                var history = versions.Where(row => row != current && row != editable).ToList();
                var latest = versions[0];
                return new TemplateGroup(group.Key, latest.Code, latest.Name, latest.Kind, current, editable, history);
            })
            .OrderBy(group => group.Code, StringComparer.Ordinal)];
    }
    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
