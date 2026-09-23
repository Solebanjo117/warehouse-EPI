using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Locations;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Pages.Admin.Catalogs.Products;

[Authorize(Policy = "AdminOnly")]
public sealed class EditModel(
    WarehouseDbContext dbContext,
    ProductLocationAssignmentService assignmentService,
    ProductionTraceabilityService production,
    ProductionWipDefaultService wipDefaults,
    ProductionService productionService, IStringLocalizer<CatalogTexts> text) : PageModel, IProductFormPage
{
    [BindProperty] public ProductInputModel Input { get; set; } = new();
    [BindProperty] public RecipeInputModel Recipe { get; set; } = new();
    [BindProperty] public RouteInputModel Route { get; set; } = new();
    [BindProperty] public MaterialWipInputModel Wip { get; set; } = new();
    public MaterialWipDefaultsView? WipConfiguration { get; private set; }
    public IReadOnlyList<SelectListItem> Units { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Types { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Classes { get; private set; } = [];
    public ProductEntryLocationOption? SelectedEntryLocation { get; private set; }
    public IReadOnlyList<LocationAssignmentRow> LocationAssignments { get; private set; } = [];
    public IReadOnlyList<LocationSearchRow> LocationResults { get; private set; } = [];
    public string? LocationSearch { get; private set; }
    public string? AssignedLocationSearch { get; private set; }
    public string AssignmentStatus { get; private set; } = "active";
    public int AssignmentPage { get; private set; } = 1;
    public int AssignmentTotalPages { get; private set; }
    public int AssignmentTotalCount { get; private set; }
    public int ActiveAssignmentCount { get; private set; }
    public string ActiveEditorSection { get; private set; } = "general";
    public string ActiveProductionSection { get; private set; } = "materials";
    public ProductionProductConfigurationView? Production { get; private set; }
    public IReadOnlyList<Product> RecipeMaterials { get; private set; } = [];
    public IReadOnlyList<ProductionStageChoice> AvailableProductionStages { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, string? locationSearch, CancellationToken token,
        string? assignedLocationSearch = null, string? assignmentStatus = null, int assignmentPage = 1)
    {
        LocationSearch = locationSearch?.Trim();
        AssignedLocationSearch = assignedLocationSearch?.Trim();
        AssignmentStatus = NormalizeAssignmentStatus(assignmentStatus);
        AssignmentPage = Math.Max(1, assignmentPage);
        if (!await LoadProductAsync(id, token)) return NotFound();
        await LoadAsync(initializeRecipe: true, token);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        ValidateOnly(Input, nameof(Input));
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
            ModelState.AddModelError("Input.Sku", text["No fue posible guardar; verifique que el SKU no esté repetido."].Value);
            await LoadAsync(initializeRecipe: true, token);
            return Page();
        }

        TempData["Success"] = text["Producto actualizado."].Value;
        return RedirectToPage("Details", new { id = Input.Id });
    }

    public async Task<IActionResult> OnPostRecipeAsync(Guid id, CancellationToken token)
    {
        ValidateOnly(Recipe, nameof(Recipe));
        var attemptedLines = Recipe.Lines.Where(x => x.MaterialProductId.HasValue || x.StageId.HasValue ||
            x.Quantity.HasValue || !string.IsNullOrWhiteSpace(x.MaterialSearch)).ToArray();
        if (attemptedLines.Any(x => !x.MaterialProductId.HasValue || x.Quantity is null or <= 0))
            ModelState.AddModelError("Recipe.Lines", text["Cada línea iniciada requiere un material válido y una cantidad positiva."].Value);
        var lines = attemptedLines
            .Where(x => x.MaterialProductId.HasValue && x.Quantity > 0)
            .Select(x => new RecipeLineInput(x.MaterialProductId!.Value, x.StageId, x.Quantity!.Value)).ToArray();

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
            TempData["Success"] = text["Nueva versión de receta guardada."].Value;
            return RedirectToPage("Edit", null, new { id }, "product-production");
        }

        ModelState.AddModelError(string.Empty, result.Errors?.FirstOrDefault() ?? text["No fue posible guardar la receta."].Value);
        ActiveEditorSection = "production";
        if (!await LoadProductAsync(id, token)) return NotFound();
        await LoadAsync(initializeRecipe: false, token);
        return Page();
    }

    public async Task<IActionResult> OnPostRouteAsync(Guid id, CancellationToken token)
    {
        ValidateOnly(Route, nameof(Route));
        var selected = Route.Stages.Where(x => x.Order.HasValue).ToArray();
        if (selected.Length == 0)
            ModelState.AddModelError("Route.Stages", text["Indica el orden de al menos un proceso."].Value);
        if (selected.Any(x => x.StageId == Guid.Empty || x.Order <= 0))
            ModelState.AddModelError("Route.Stages", text["Cada proceso incluido requiere un orden mayor que cero."].Value);
        if (selected.Where(x => x.Order.HasValue).GroupBy(x => x.Order!.Value).Any(x => x.Count() > 1))
            ModelState.AddModelError("Route.Stages", text["No repitas el mismo número de orden en dos procesos."].Value);
        if (selected.GroupBy(x => x.StageId).Any(x => x.Count() > 1))
            ModelState.AddModelError("Route.Stages", text["Cada proceso sólo puede incluirse una vez."].Value);

        if (!ModelState.IsValid)
        {
            ActiveEditorSection = "production";
            ActiveProductionSection = "route";
            Route.Pin = "";
            ModelState.Remove("Route.Pin");
            if (!await LoadProductAsync(id, token)) return NotFound();
            await LoadAsync(initializeRecipe: true, token);
            return Page();
        }
        var stageIds = selected.OrderBy(x => x.Order).Select(x => x.StageId).ToArray();
        var result = await productionService.CreateRouteAsync(new(Route.OperationId, id, Route.Name, stageIds, Route.Pin), token);
        Route.Pin = "";
        ModelState.Remove("Route.Pin");

        if (result.Status == ProductionCommandStatus.Success)
        {
            TempData["Success"] = text["Ruta creada. Ya puedes capturar la receta y sus materiales."].Value;
            return RedirectToPage("Edit", null, new { id }, "product-production-route");
        }

        ModelState.AddModelError(string.Empty, result.Status == ProductionCommandStatus.InvalidPin
            ? text["NIP ADMIN inválido."].Value
            : result.ValidationErrors.FirstOrDefault() ?? text["No fue posible crear la ruta."].Value);
        ActiveEditorSection = "production";
        ActiveProductionSection = "route";
        if (!await LoadProductAsync(id, token)) return NotFound();
        await LoadAsync(initializeRecipe: true, token);
        return Page();
    }

    public async Task<IActionResult> OnPostAssignLocationAsync(Guid id, Guid locationId, CancellationToken token,
        string? locationSearch = null, string? assignedLocationSearch = null, string? assignmentStatus = null,
        int assignmentPage = 1)
    {
        var result = await assignmentService.AssignAsync(id, locationId, token);
        if (result == ProductLocationAssignmentResult.Success) TempData["Success"] = text["Ubicación asignada al producto."].Value;
        else TempData["Error"] = AssignmentError(result);
        return RedirectToLocations(id, locationSearch, assignedLocationSearch, assignmentStatus, assignmentPage);
    }

    public async Task<IActionResult> OnGetWipTargetsAsync(Guid stageId, string? q, CancellationToken token) =>
        new JsonResult(await wipDefaults.SearchAsync(stageId, q, token));

    public async Task<IActionResult> OnPostWipAsync(Guid id, CancellationToken token)
    {
        ValidateOnly(Wip, nameof(Wip));
        var attempted = Wip.Rules.Where(x => x.StageId.HasValue || !string.IsNullOrWhiteSpace(x.TargetKey)).ToArray();
        if (attempted.Any(x => !x.StageId.HasValue || string.IsNullOrWhiteSpace(x.TargetKey)))
            ModelState.AddModelError("Wip.Rules", text["Cada regla requiere proceso y destino WIP."].Value);
        if (string.IsNullOrWhiteSpace(Wip.Reason) || string.IsNullOrWhiteSpace(Wip.Pin))
            ModelState.AddModelError("Wip.Reason", text["Indica motivo y NIP ADMIN."].Value);
        var rules = attempted.Where(x => x.StageId.HasValue && !string.IsNullOrWhiteSpace(x.TargetKey))
            .Select(x => new MaterialWipRuleInput(x.StageId!.Value, x.TargetKey!)).ToArray();
        var result = ModelState.IsValid
            ? await wipDefaults.SaveMaterialAsync(new(Wip.OperationId, id, Wip.ExpectedVersion, rules, Wip.Reason!, Wip.Pin!), token)
            : new WipDefaultResult(WipDefaultStatus.ValidationFailed, [text["Revisa las reglas, el motivo y el NIP ADMIN."].Value]);
        Wip.Pin = ""; ModelState.Remove("Wip.Pin");
        if (result.Status == WipDefaultStatus.Success)
        {
            TempData["Success"] = text["Destinos WIP actualizados."].Value;
            return RedirectToPage("Edit", null, new { id }, "product-wip-defaults");
        }
        ModelState.AddModelError(string.Empty, result.Status switch
        {
            WipDefaultStatus.InvalidPin => text["NIP ADMIN inválido."].Value,
            WipDefaultStatus.ConcurrencyConflict => text["La configuración WIP cambió. Recarga y vuelve a revisar."].Value,
            WipDefaultStatus.IdempotencyConflict => text["La operación ya se utilizó con datos distintos."].Value,
            _ => result.Errors?.FirstOrDefault() ?? text["No fue posible guardar los destinos WIP."].Value
        });
        ActiveEditorSection = "wip";
        if (!await LoadProductAsync(id, token)) return NotFound();
        await LoadAsync(initializeRecipe: true, token, initializeWip: false);
        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateLocationAsync(Guid id, Guid locationId, CancellationToken token,
        string? locationSearch = null, string? assignedLocationSearch = null, string? assignmentStatus = null,
        int assignmentPage = 1)
    {
        var result = await assignmentService.DeactivateAsync(id, locationId, token);
        if (result == ProductLocationAssignmentResult.Success) TempData["Success"] = text["La asignación fue desactivada."].Value;
        else if (result == ProductLocationAssignmentResult.SuccessDefaultEntryCleared) TempData["Success"] = text["La asignación fue desactivada y la ubicación principal de entrada fue retirada."].Value;
        else TempData["Error"] = text["La asignación activa ya no existe."].Value;
        return RedirectToLocations(id, locationSearch, assignedLocationSearch, assignmentStatus, assignmentPage);
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

    private async Task LoadAsync(bool initializeRecipe, CancellationToken token, bool initializeWip = true)
    {
        (Units, Types, Classes, SelectedEntryLocation) = await ProductPageSupport.LoadOptionsAsync(dbContext, Input, token);
        var assignments = dbContext.ProductLocationAssignments.AsNoTracking().Where(x => x.ProductId == Input.Id);
        ActiveAssignmentCount = await assignments.CountAsync(x => x.IsActive, token);
        if (AssignmentStatus == "active") assignments = assignments.Where(x => x.IsActive);
        else if (AssignmentStatus == "inactive") assignments = assignments.Where(x => !x.IsActive);
        if (!string.IsNullOrWhiteSpace(AssignedLocationSearch))
        {
            var assignedTerm = AssignedLocationSearch.ToUpperInvariant();
            assignments = assignments.Where(x => x.Location.Code.Contains(assignedTerm) ||
                (x.Location.Description != null && x.Location.Description.ToUpper().Contains(assignedTerm)));
        }
        AssignmentTotalCount = await assignments.CountAsync(token);
        AssignmentTotalPages = (int)Math.Ceiling(AssignmentTotalCount / 25d);
        if (AssignmentTotalPages > 0) AssignmentPage = Math.Min(AssignmentPage, AssignmentTotalPages);
        else AssignmentPage = 1;
        LocationAssignments = await assignments
            .OrderByDescending(x => x.IsActive).ThenBy(x => x.Location.RowCode).ThenBy(x => x.Location.RackNumber)
            .ThenBy(x => x.Location.PalletNumber).ThenBy(x => x.Location.Code)
            .Skip((AssignmentPage - 1) * 25).Take(25)
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
        if (Production?.RouteId is null && Production?.ProductIsActive == true)
        {
            AvailableProductionStages = await dbContext.ProductionStages.AsNoTracking().Where(x => x.IsActive)
                .OrderBy(x => x.Code).ThenBy(x => x.Name)
                .Select(x => new ProductionStageChoice(x.Id, x.Code, x.Name)).ToListAsync(token);
            var currentOrders = Route.Stages.Where(x => x.StageId != Guid.Empty)
                .GroupBy(x => x.StageId).ToDictionary(x => x.Key, x => x.First().Order);
            Route.Stages = AvailableProductionStages.Select(x => new RouteStageOrderInputModel
            {
                StageId = x.Id,
                Order = currentOrders.GetValueOrDefault(x.Id)
            }).ToList();
        }
        WipConfiguration = await wipDefaults.GetMaterialAsync(Input.Id, token);
        if (initializeWip && Wip.Rules.Count == 0 && WipConfiguration is not null)
        {
            Wip = new MaterialWipInputModel
            {
                ExpectedVersion = WipConfiguration.Version,
                Rules = WipConfiguration.Rules.Select(x => new MaterialWipRuleInputModel
                {
                    StageId = x.StageId,
                    TargetKey = x.TargetKey,
                    TargetLabel = x.Target
                }).ToList()
            };
        }
        if (initializeRecipe) InitializeRecipe();
        if (Recipe.Lines.Count == 0) Recipe.Lines.Add(new());
        var materialIds = Recipe.Lines.Where(x => x.MaterialProductId.HasValue)
            .Select(x => x.MaterialProductId!.Value).Distinct().ToArray();
        RecipeMaterials = materialIds.Length == 0 ? [] : await dbContext.Products.AsNoTracking()
            .Include(x => x.BaseUnit).Where(x => materialIds.Contains(x.Id)).ToListAsync(token);
    }

    private RedirectToPageResult RedirectToLocations(Guid id, string? locationSearch,
        string? assignedLocationSearch, string? assignmentStatus, int assignmentPage) =>
        RedirectToPage("Edit", null, new
        {
            id,
            locationSearch = locationSearch?.Trim(),
            assignedLocationSearch = assignedLocationSearch?.Trim(),
            assignmentStatus = NormalizeAssignmentStatus(assignmentStatus),
            assignmentPage = Math.Max(1, assignmentPage)
        }, "product-locations");

    private static string NormalizeAssignmentStatus(string? value) => value switch
    {
        "inactive" => "inactive",
        "all" => "all",
        _ => "active"
    };

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

    private void ValidateOnly(object model, string prefix)
    {
        ModelState.Clear();
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        foreach (var result in results)
        {
            var members = result.MemberNames.DefaultIfEmpty(string.Empty);
            foreach (var member in members)
                ModelState.AddModelError(string.IsNullOrEmpty(member) ? prefix : $"{prefix}.{member}",
                    result.ErrorMessage ?? text["El valor no es válido."].Value);
        }
    }

    private string AssignmentError(ProductLocationAssignmentResult result) => result switch
    {
        ProductLocationAssignmentResult.AlreadyActive => text["El producto ya está asignado a esa ubicación."].Value,
        ProductLocationAssignmentResult.ProductInactive => text["No se puede asignar un producto inactivo."].Value,
        ProductLocationAssignmentResult.LocationInactive => text["No se puede asignar a una ubicación inactiva."].Value,
        ProductLocationAssignmentResult.LocationBlocked => text["No se puede asignar a una ubicación bloqueada."].Value,
        ProductLocationAssignmentResult.LocationDoesNotTrackInventory => text["La ubicación no admite asignaciones de inventario."].Value,
        _ => text["El producto o la ubicación ya no existe."].Value
    };

    public sealed class RecipeInputModel
    {
        [Range(typeof(decimal), "0.0001", "99999999999999")]
        public decimal BaseQuantity { get; set; } = 1;
        public List<RecipeLineInputModel> Lines { get; set; } = [];
        [Required, StringLength(500)] public string Reason { get; set; } = "Definición inicial";
        [Required(ErrorMessage = "El NIP ADMIN es obligatorio."),
         RegularExpression("^[0-9]{4,8}$", ErrorMessage = "El NIP ADMIN debe contener de 4 a 8 dígitos.")]
        public string Pin { get; set; } = "";
    }

    public sealed class RecipeLineInputModel
    {
        public Guid? MaterialProductId { get; set; }
        public string? MaterialSearch { get; set; }
        public Guid? StageId { get; set; }
        public decimal? Quantity { get; set; }
    }

    public sealed class RouteInputModel
    {
        public Guid OperationId { get; set; } = Guid.NewGuid();
        [Required(ErrorMessage = "El nombre de la ruta es obligatorio."), StringLength(120)]
        public string Name { get; set; } = "Ruta de producción";
        public List<RouteStageOrderInputModel> Stages { get; set; } = [];
        [Required(ErrorMessage = "El NIP ADMIN es obligatorio."),
         RegularExpression("^[0-9]{4,8}$", ErrorMessage = "El NIP ADMIN debe contener de 4 a 8 dígitos.")]
        public string Pin { get; set; } = "";
    }

    public sealed class RouteStageOrderInputModel
    {
        public Guid StageId { get; set; }
        public int? Order { get; set; }
    }

    public sealed record ProductionStageChoice(Guid Id, string Code, string Name);

    public sealed record LocationAssignmentRow(Guid LocationId, string Code, string? Description,
        bool LocationIsActive, bool LocationIsBlocked, bool IsActive, bool IsDefaultEntry);
    public sealed record LocationSearchRow(Guid Id, string Code, string? Description, bool IsAssigned);
}
