using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class EditModel(
    WarehouseDbContext dbContext,
    ProductLocationAssignmentService assignmentService,
    ProductionTraceabilityService production,
    ProductionWipDefaultService wipDefaults) : PageModel, IProductFormPage
{
    [BindProperty] public ProductInputModel Input { get; set; } = new();
    [BindProperty] public RecipeInputModel Recipe { get; set; } = new();
    [BindProperty] public MaterialWipInputModel Wip { get; set; } = new();
    public MaterialWipDefaultsView? WipConfiguration { get; private set; }
    public IReadOnlyList<SelectListItem> Units { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Types { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Classes { get; private set; } = [];
    public ProductEntryLocationOption? SelectedEntryLocation { get; private set; }
    public IReadOnlyList<LocationAssignmentRow> LocationAssignments { get; private set; } = [];
    public IReadOnlyList<LocationSearchRow> LocationResults { get; private set; } = [];
    public string? LocationSearch { get; private set; }
    public ProductionProductConfigurationView? Production { get; private set; }
    public IReadOnlyList<Product> RecipeMaterials { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, string? locationSearch, CancellationToken token)
    {
        LocationSearch = locationSearch?.Trim();
        if (!await LoadProductAsync(id, token)) return NotFound();
        await LoadAsync(initializeRecipe: true, token);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        RemoveModelStatePrefix(nameof(Recipe));
        ProductPageSupport.Normalize(Input);
        await ProductPageSupport.ValidateAsync(dbContext, Input, ModelState, token);
        var product = await dbContext.Products.SingleOrDefaultAsync(x => x.Id == Input.Id, token);
        if (product is null) return NotFound();
        if (!ModelState.IsValid)
        {
            await LoadAsync(initializeRecipe: true, token);
            return Page();
        }

        ProductPageSupport.Apply(product, Input);
        await ProductPageSupport.EnsureDefaultEntryAssignmentAsync(dbContext, product, token);
        try
        {
            await dbContext.SaveChangesAsync(token);
        }
        catch (DbUpdateException)
        {
            ModelState.AddModelError("Input.Sku", "No fue posible guardar; verifique que el SKU no esté repetido.");
            await LoadAsync(initializeRecipe: true, token);
            return Page();
        }

        TempData["Success"] = "Producto actualizado.";
        return RedirectToPage("Details", new { id = Input.Id });
    }

    public async Task<IActionResult> OnPostRecipeAsync(Guid id, CancellationToken token)
    {
        RemoveModelStatePrefix(nameof(Input));
        var attemptedLines = Recipe.Lines.Where(x => x.MaterialProductId.HasValue || x.StageId.HasValue ||
            x.Quantity.HasValue || !string.IsNullOrWhiteSpace(x.MaterialSearch)).ToArray();
        if (attemptedLines.Any(x => !x.MaterialProductId.HasValue || !x.StageId.HasValue || x.Quantity is null or <= 0))
            ModelState.AddModelError(string.Empty, "Cada material utilizado requiere una selección válida, proceso y cantidad positiva.");
        var lines = attemptedLines
            .Where(x => x.MaterialProductId.HasValue && x.StageId.HasValue && x.Quantity > 0)
            .Select(x => new RecipeLineInput(x.MaterialProductId!.Value, x.StageId!.Value, x.Quantity!.Value)).ToArray();

        ProductionTraceabilityResult result;
        if (!ModelState.IsValid)
        {
            result = new(false, Errors: ["Revisa la cantidad base, el motivo y el NIP ADMIN."]);
        }
        else
        {
            result = await production.SaveRecipeAsync(new(id, Recipe.BaseQuantity, lines, Recipe.Reason, Recipe.Pin), token);
        }
        Recipe.Pin = "";
        ModelState.Remove("Recipe.Pin");

        if (result.Success)
        {
            TempData["Success"] = "Nueva versión de receta guardada.";
            return RedirectToPage("Edit", null, new { id }, "product-production");
        }

        ModelState.AddModelError(string.Empty, result.Errors?.FirstOrDefault() ?? "No fue posible guardar la receta.");
        if (!await LoadProductAsync(id, token)) return NotFound();
        await LoadAsync(initializeRecipe: false, token);
        return Page();
    }

    public async Task<IActionResult> OnPostAssignLocationAsync(Guid id, Guid locationId, CancellationToken token)
    {
        var result = await assignmentService.AssignAsync(id, locationId, token);
        if (result == ProductLocationAssignmentResult.Success) TempData["Success"] = "Ubicación asignada al producto.";
        else TempData["Error"] = AssignmentError(result);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetWipTargetsAsync(Guid stageId, string? q, CancellationToken token) =>
        new JsonResult(await wipDefaults.SearchAsync(stageId, q, token));

    public async Task<IActionResult> OnPostWipAsync(Guid id, CancellationToken token)
    {
        RemoveModelStatePrefix(nameof(Input)); RemoveModelStatePrefix(nameof(Recipe));
        var attempted = Wip.Rules.Where(x => x.StageId.HasValue || !string.IsNullOrWhiteSpace(x.TargetKey)).ToArray();
        if (attempted.Any(x => !x.StageId.HasValue || string.IsNullOrWhiteSpace(x.TargetKey)))
            ModelState.AddModelError("Wip.Rules", "Cada regla requiere proceso y destino WIP.");
        if (string.IsNullOrWhiteSpace(Wip.Reason) || string.IsNullOrWhiteSpace(Wip.Pin))
            ModelState.AddModelError("Wip.Reason", "Indica motivo y NIP ADMIN.");
        var rules = attempted.Where(x => x.StageId.HasValue && !string.IsNullOrWhiteSpace(x.TargetKey))
            .Select(x => new MaterialWipRuleInput(x.StageId!.Value, x.TargetKey!)).ToArray();
        var result = ModelState.IsValid
            ? await wipDefaults.SaveMaterialAsync(new(Wip.OperationId, id, Wip.ExpectedVersion, rules, Wip.Reason!, Wip.Pin!), token)
            : new WipDefaultResult(WipDefaultStatus.ValidationFailed, ["Revisa las reglas, el motivo y el NIP ADMIN."]);
        Wip.Pin = ""; ModelState.Remove("Wip.Pin");
        if (result.Status == WipDefaultStatus.Success)
        {
            TempData["Success"] = "Destinos WIP actualizados.";
            return RedirectToPage("Edit", null, new { id }, "product-wip-defaults");
        }
        ModelState.AddModelError(string.Empty, result.Status switch
        {
            WipDefaultStatus.InvalidPin => "NIP ADMIN inválido.",
            WipDefaultStatus.ConcurrencyConflict => "La configuración WIP cambió. Recarga y vuelve a revisar.",
            WipDefaultStatus.IdempotencyConflict => "La operación ya se utilizó con datos distintos.",
            _ => result.Errors?.FirstOrDefault() ?? "No fue posible guardar los destinos WIP."
        });
        if (!await LoadProductAsync(id, token)) return NotFound();
        await LoadAsync(initializeRecipe: true, token);
        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateLocationAsync(Guid id, Guid locationId, CancellationToken token)
    {
        var result = await assignmentService.DeactivateAsync(id, locationId, token);
        if (result == ProductLocationAssignmentResult.Success) TempData["Success"] = "La asignación fue desactivada.";
        else if (result == ProductLocationAssignmentResult.SuccessDefaultEntryCleared) TempData["Success"] = "La asignación fue desactivada y la ubicación principal de entrada fue retirada.";
        else TempData["Error"] = "La asignación activa ya no existe.";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadProductAsync(Guid id, CancellationToken token)
    {
        var product = await dbContext.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (product is null) return false;
        Input = new ProductInputModel
        {
            Id = product.Id,
            Sku = product.Sku,
            Description = product.Description,
            ExternalReference = product.ExternalReference,
            ProductTypeId = product.ProductTypeId,
            ProductClassId = product.ProductClassId,
            BaseUnitId = product.BaseUnitId,
            MinimumStock = product.MinimumStock,
            DefaultEntryLocationId = product.DefaultEntryLocationId,
            IsActive = product.IsActive
        };
        return true;
    }

    private async Task LoadAsync(bool initializeRecipe, CancellationToken token)
    {
        (Units, Types, Classes, SelectedEntryLocation) = await ProductPageSupport.LoadOptionsAsync(dbContext, Input, token);
        LocationAssignments = await dbContext.ProductLocationAssignments.AsNoTracking().Where(x => x.ProductId == Input.Id)
            .OrderByDescending(x => x.IsActive).ThenBy(x => x.Location.RowCode).ThenBy(x => x.Location.RackNumber)
            .ThenBy(x => x.Location.PalletNumber).ThenBy(x => x.Location.Code)
            .Select(x => new LocationAssignmentRow(x.LocationId, x.Location.Code, x.Location.Description,
                x.Location.IsActive, x.Location.IsBlocked, x.IsActive, x.LocationId == Input.DefaultEntryLocationId))
            .ToListAsync(token);
        if (!string.IsNullOrWhiteSpace(LocationSearch))
        {
            var term = LocationSearch.ToUpperInvariant();
            LocationResults = await dbContext.Locations.AsNoTracking()
                .Where(x => x.IsPhysicallyPresent && x.IsActive && !x.IsBlocked &&
                    (x.Code.Contains(term) || (x.Description != null && x.Description.ToUpper().Contains(term))))
                .OrderBy(x => x.RowCode).ThenBy(x => x.RackNumber).ThenBy(x => x.PalletNumber).ThenBy(x => x.Code)
                .Take(20)
                .Select(x => new LocationSearchRow(x.Id, x.Code, x.Description,
                    x.ProductAssignments.Any(a => a.ProductId == Input.Id && a.IsActive))).ToListAsync(token);
        }

        Production = await production.GetProductConfigurationAsync(Input.Id, token);
        WipConfiguration = await wipDefaults.GetMaterialAsync(Input.Id, token);
        if (Wip.Rules.Count == 0 && WipConfiguration is not null)
        {
            Wip = new MaterialWipInputModel
            {
                ExpectedVersion = WipConfiguration.Version,
                Rules = WipConfiguration.Rules.Select(x => new MaterialWipRuleInputModel
                {
                    StageId = x.StageId, TargetKey = x.TargetKey, TargetLabel = x.Target
                }).ToList()
            };
        }
        while (Wip.Rules.Count < Math.Max(4, WipConfiguration?.Processes.Count ?? 0)) Wip.Rules.Add(new());
        if (initializeRecipe) InitializeRecipe();
        while (Recipe.Lines.Count < 8) Recipe.Lines.Add(new());
        var materialIds = Recipe.Lines.Where(x => x.MaterialProductId.HasValue)
            .Select(x => x.MaterialProductId!.Value).Distinct().ToArray();
        RecipeMaterials = materialIds.Length == 0 ? [] : await dbContext.Products.AsNoTracking()
            .Include(x => x.BaseUnit).Where(x => materialIds.Contains(x.Id)).ToListAsync(token);
    }

    private void InitializeRecipe()
    {
        var current = Production?.ActiveRecipe;
        Recipe = current is null
            ? new RecipeInputModel { Reason = "Definición inicial" }
            : new RecipeInputModel
            {
                BaseQuantity = current.BaseQuantity,
                Reason = "",
                Lines = current.Lines.Select(x => new RecipeLineInputModel
                {
                    MaterialProductId = x.MaterialProductId,
                    MaterialSearch = x.Material.Split(" · ")[0],
                    StageId = x.StageId,
                    Quantity = x.Quantity
                }).ToList()
            };
    }

    private void RemoveModelStatePrefix(string prefix)
    {
        foreach (var key in ModelState.Keys.Where(key => key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith($"{prefix}.", StringComparison.OrdinalIgnoreCase)).ToArray())
            ModelState.Remove(key);
    }

    private static string AssignmentError(ProductLocationAssignmentResult result) => result switch
    {
        ProductLocationAssignmentResult.AlreadyActive => "El producto ya está asignado a esa ubicación.",
        ProductLocationAssignmentResult.ProductInactive => "No se puede asignar un producto inactivo.",
        ProductLocationAssignmentResult.LocationInactive => "No se puede asignar a una ubicación inactiva.",
        ProductLocationAssignmentResult.LocationBlocked => "No se puede asignar a una ubicación bloqueada.",
        ProductLocationAssignmentResult.LocationDoesNotTrackInventory => "La ubicación no admite asignaciones de inventario.",
        _ => "El producto o la ubicación ya no existe."
    };

    public sealed class RecipeInputModel
    {
        [Range(typeof(decimal), "0.0001", "99999999999999")]
        public decimal BaseQuantity { get; set; } = 1;
        public List<RecipeLineInputModel> Lines { get; set; } = [];
        [Required, StringLength(500)] public string Reason { get; set; } = "Definición inicial";
        [Required, RegularExpression("^[0-9]{4,8}$")] public string Pin { get; set; } = "";
    }

    public sealed class RecipeLineInputModel
    {
        public Guid? MaterialProductId { get; set; }
        public string? MaterialSearch { get; set; }
        public Guid? StageId { get; set; }
        public decimal? Quantity { get; set; }
    }

    public sealed record LocationAssignmentRow(Guid LocationId, string Code, string? Description,
        bool LocationIsActive, bool LocationIsBlocked, bool IsActive, bool IsDefaultEntry);
    public sealed record LocationSearchRow(Guid Id, string Code, string? Description, bool IsAssigned);
}
