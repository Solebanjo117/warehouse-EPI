using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class CreateModel(WarehouseDbContext dbContext, ProductionWipDefaultService wipDefaults) : PageModel, IProductFormPage
{
    [BindProperty] public ProductInputModel Input { get; set; } = new();
    [BindProperty] public MaterialWipInputModel Wip { get; set; } = new();
    public MaterialWipDefaultsView? WipConfiguration { get; private set; }
    public IReadOnlyList<SelectListItem> Units { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Types { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Classes { get; private set; } = [];
    public ProductEntryLocationOption? SelectedEntryLocation { get; private set; }

    public async Task OnGetAsync(CancellationToken token) => await LoadAsync(token);
    public async Task<IActionResult> OnGetWipTargetsAsync(Guid stageId, string? q, CancellationToken token) =>
        new JsonResult(await wipDefaults.SearchAsync(stageId, q, token));

    public async Task<IActionResult> OnPostAsync(string? nextStep, CancellationToken token)
    {
        ProductPageSupport.Normalize(Input);
        await ProductPageSupport.ValidateAsync(dbContext, Input, ModelState, token);
        var attempted = Wip.Rules.Where(x => x.StageId.HasValue || !string.IsNullOrWhiteSpace(x.TargetKey)).ToArray();
        if (attempted.Any(x => !x.StageId.HasValue || string.IsNullOrWhiteSpace(x.TargetKey)))
            ModelState.AddModelError("Wip.Rules", "Cada regla requiere proceso y destino WIP.");
        if (attempted.Length > 0 && (string.IsNullOrWhiteSpace(Wip.Reason) || string.IsNullOrWhiteSpace(Wip.Pin)))
            ModelState.AddModelError("Wip.Reason", "Indica motivo y NIP ADMIN para guardar destinos WIP.");
        if (!ModelState.IsValid) { Wip.Pin = ""; await LoadAsync(token); return Page(); }

        var product = new Product { Sku = Input.Sku };
        ProductPageSupport.Apply(product, Input);
        dbContext.Products.Add(product);
        await ProductPageSupport.EnsureDefaultEntryAssignmentAsync(dbContext, product, token);
        await using var transaction = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(token) : null;
        try
        {
            await dbContext.SaveChangesAsync(token);
            if (attempted.Length > 0)
            {
                var rules = attempted.Select(x => new MaterialWipRuleInput(x.StageId!.Value, x.TargetKey!)).ToArray();
                var result = await wipDefaults.SaveMaterialAsync(new(Wip.OperationId, product.Id, Wip.ExpectedVersion,
                    rules, Wip.Reason!, Wip.Pin!), token);
                Wip.Pin = "";
                if (result.Status != WipDefaultStatus.Success)
                {
                    if (transaction is not null) await transaction.RollbackAsync(token);
                    ModelState.AddModelError(string.Empty, WipError(result));
                    await LoadAsync(token);
                    return Page();
                }
            }
            if (transaction is not null) await transaction.CommitAsync(token);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            ModelState.AddModelError("Input.Sku", "No fue posible guardar; verifique que el SKU no esté repetido.");
            Wip.Pin = "";
            await LoadAsync(token);
            return Page();
        }
        TempData["Success"] = "Producto creado.";
        if (string.Equals(nextStep, "production", StringComparison.Ordinal))
            return RedirectToPage("Edit", null, new { id = product.Id }, "product-production");
        return RedirectToPage("Details", new { id = product.Id });
    }

    private async Task LoadAsync(CancellationToken token)
    {
        (Units, Types, Classes, SelectedEntryLocation) = await ProductPageSupport.LoadOptionsAsync(dbContext, Input, token);
        var setup = await wipDefaults.GetSetupAsync(token);
        WipConfiguration = new(setup.Version, [], setup.Processes, []);
        if (Wip.ExpectedVersion == 0) Wip.ExpectedVersion = setup.Version;
    }
    private static string WipError(WipDefaultResult result) => result.Status switch
    {
        WipDefaultStatus.InvalidPin => "NIP ADMIN inválido.",
        WipDefaultStatus.ConcurrencyConflict => "La configuración WIP cambió. Recarga y vuelve a revisar.",
        _ => result.Errors?.FirstOrDefault() ?? "No fue posible guardar los destinos WIP."
    };
}
