using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Labels;

namespace WarehouseEPI.Web.Pages.Admin.Labels.Templates;

public sealed class EditModel(LabelTemplateService templates, LabelAssetService assets) : PageModel
{
    public LabelVersionEditor Version { get; private set; } = null!;
    public IReadOnlyList<LabelAssetView> Assets { get; private set; } = [];
    public IReadOnlyList<LabelSizeDefinition> Sizes => LabelSizeRegistry.All;
    public bool Editable => Version.Status is LabelTemplateStatus.Draft or LabelTemplateStatus.InValidation;

    [BindProperty] public InputModel Input { get; set; } = new();
    public sealed class InputModel
    {
        public Guid VersionId { get; set; }
        [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
        public LabelSizePreset Size { get; set; }
        [Required] public string DesignJson { get; set; } = string.Empty;
        public uint RowVersion { get; set; }
        public bool AcknowledgeWarnings { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken token)
    {
        if (!await LoadAsync(id, token)) return NotFound();
        Input = new() { VersionId = Version.VersionId, Name = Version.Name, Size = Version.SizePreset, DesignJson = Version.DesignJson, RowVersion = Version.RowVersion };
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken token)
    {
        if (ModelState.IsValid)
        {
            var result = await templates.SaveAsync(CurrentUserId(), Input.VersionId, Input.Name, Input.Size, Input.DesignJson, Input.RowVersion, Input.AcknowledgeWarnings, token);
            if (result.Status == LabelTemplateMutationStatus.Success) { TempData["StatusMessage"] = "Borrador guardado."; return RedirectToPage(new { id = Input.VersionId }); }
            AddErrors(result);
        }
        if (!await LoadAsync(Input.VersionId, token)) return NotFound();
        return Page();
    }

    public Task<IActionResult> OnPostSubmitAsync(CancellationToken token) => SaveThenMutateAsync(() => templates.SubmitAsync(CurrentUserId(), Input.VersionId, Input.AcknowledgeWarnings, token), "Versión enviada a validación.", token);
    public Task<IActionResult> OnPostReturnAsync(CancellationToken token) => MutateAsync(() => templates.ReturnToDraftAsync(CurrentUserId(), Input.VersionId, token), "Versión devuelta a borrador.", token);
    public Task<IActionResult> OnPostPublishAsync(CancellationToken token) => SaveThenMutateAsync(() => templates.PublishAsync(CurrentUserId(), Input.VersionId, Input.AcknowledgeWarnings, token), "Versión publicada sin NIP.", token);

    private async Task<IActionResult> SaveThenMutateAsync(Func<Task<LabelTemplateMutationResult>> action, string success, CancellationToken token)
    {
        if (ModelState.IsValid)
        {
            var saved = await templates.SaveAsync(CurrentUserId(), Input.VersionId, Input.Name, Input.Size, Input.DesignJson, Input.RowVersion, Input.AcknowledgeWarnings, token);
            if (saved.Status == LabelTemplateMutationStatus.Success) return await MutateAsync(action, success, token);
            AddErrors(saved);
        }
        if (!await LoadAsync(Input.VersionId, token)) return NotFound();
        return Page();
    }

    private async Task<IActionResult> MutateAsync(Func<Task<LabelTemplateMutationResult>> action, string success, CancellationToken token)
    {
        var result = await action();
        if (result.Status == LabelTemplateMutationStatus.Success) { TempData["StatusMessage"] = success; return RedirectToPage(new { id = Input.VersionId }); }
        AddErrors(result);
        if (!await LoadAsync(Input.VersionId, token)) return NotFound();
        return Page();
    }

    private void AddErrors(LabelTemplateMutationResult result)
    {
        var fallback = result.Status == LabelTemplateMutationStatus.Conflict ? "La versión cambió en otra sesión; el JSON capturado se conserva abajo." : "No fue posible completar la operación.";
        foreach (var error in result.Errors ?? [fallback]) ModelState.AddModelError(string.Empty, error);
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken token)
    {
        var version = await templates.GetEditorAsync(id, token);
        if (version is null) return false;
        Version = version;
        Assets = await assets.GetAllAsync(token);
        return true;
    }
    private Guid CurrentUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
