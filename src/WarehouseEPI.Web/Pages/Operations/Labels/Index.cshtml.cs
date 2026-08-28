using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Operations.Labels;

public sealed class IndexModel(LabelTemplateService templates, LabelDocumentService documents,
    OperationalInventoryQueryService products, WarehouseClock warehouseClock, TimeProvider timeProvider) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public Guid? Template { get; set; }
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    public IReadOnlyList<LabelTemplateChoice> Templates { get; private set; } = [];
    public LabelTemplateChoice? SelectedTemplate { get; private set; }
    public LabelDesignDocumentV1? Design { get; private set; }
    public OperationalProductResult? SelectedProduct { get; private set; }
    public LabelRenderDocument? Preview { get; private set; }
    public IReadOnlyList<string> PrintWarnings { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken token)
    {
        await LoadTemplatesAsync(token);
        SelectedTemplate = Template is not null ? Templates.SingleOrDefault(item => item.VersionId == Template) :
            !string.IsNullOrWhiteSpace(Code) ? Templates.SingleOrDefault(item => item.Code == Code) : Templates.FirstOrDefault();
        if (SelectedTemplate is null) return;
        Input.TemplateVersionId = SelectedTemplate.VersionId;
        var entity = await templates.GetPublishedEntityAsync(SelectedTemplate.VersionId, token);
        Design = entity is null ? null : LabelDesignSerializer.Deserialize(entity.DesignJson);
        Input.Values["input.manufacturingDate"] = (await warehouseClock.GetDateAsync(timeProvider.GetUtcNow(), token)).ToString("yyyy-MM-dd");
        Input.Values["input.quantity"] = "1";
        ApplyDefaults();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        await LoadTemplatesAsync(token);
        SelectedTemplate = Templates.SingleOrDefault(item => item.VersionId == Input.TemplateVersionId);
        if (SelectedTemplate is null) ModelState.AddModelError("Input.TemplateVersionId", "La plantilla ya no está publicada.");
        var entity = SelectedTemplate is null ? null : await templates.GetPublishedEntityAsync(SelectedTemplate.VersionId, token);
        Design = entity is null ? null : LabelDesignSerializer.Deserialize(entity.DesignJson);
        if (Input.ProductId == Guid.Empty) ModelState.AddModelError("Input.ProductId", "Selecciona un producto activo.");
        else { SelectedProduct = await products.GetProductAsync(Input.ProductId, cancellationToken: token); if (SelectedProduct is null) ModelState.AddModelError("Input.ProductId", "El producto no existe o está inactivo."); }
        if (!ModelState.IsValid || entity is null || SelectedProduct is null) return Page();
        var rendered = documents.Render(entity, SelectedProduct, Input.Values, Input.Copies);
        foreach (var error in rendered.Errors) ModelState.AddModelError(string.Empty, error);
        PrintWarnings = rendered.Warnings;
        Preview = rendered.Document;
        return Page();
    }

    public bool Uses(string binding) => Design?.Elements.Any(item => item.Binding == binding) == true;
    private async Task LoadTemplatesAsync(CancellationToken token) => Templates = await templates.GetPublishedAsync(token: token);
    private void ApplyDefaults() { if (Design is null) return; foreach (var field in Design.Fields) if (!Input.Values.ContainsKey(field.Key) && field.DefaultValue is not null) Input.Values[field.Key] = field.DefaultValue; }
    public sealed class InputModel { public Guid TemplateVersionId { get; set; } public Guid ProductId { get; set; } [Range(1, 100)] public int Copies { get; set; } = 1; public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal); }
}
