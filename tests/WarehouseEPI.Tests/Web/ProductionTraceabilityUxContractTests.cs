namespace WarehouseEPI.Tests.Web;

public sealed class ProductionTraceabilityUxContractTests
{
    [Fact]
    public void Traceability_pages_keep_visual_and_operational_contracts()
    {
        var root = FindRoot();
        var trace = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "_Traceability.cshtml"));
        var recipe = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "_ProductionRecipe.cshtml"));
        var create = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "_ProductForm.cshtml"));
        var createModel = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Create.cshtml.cs"));
        var edit = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Edit.cshtml"));
        var editModel = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Edit.cshtml.cs"));
        var details = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "Details.cshtml"));
        var redirect = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Production", "Recipes.cshtml.cs"));
        Assert.Contains("Materiales", trace);
        Assert.Contains("Procesos", trace);
        Assert.Contains("Producto terminado", trace);
        Assert.Contains("Bodega", trace);
        Assert.Contains("Confirmar resultado y consumo", trace);
        Assert.Contains("data-batch-filter", trace);
        Assert.Contains("Procedencia del lote", trace);
        Assert.Contains("asp-page=\"Execution\"", trace);
        Assert.DoesNotContain("asp-page-handler=\"AdjustPlan\"", trace);
        Assert.Contains("ReverseResult", trace);
        Assert.DoesNotContain("onchange=", trace, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", trace, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Receta y materiales", recipe);
        Assert.Contains("asp-page-handler=\"Route\"", recipe, StringComparison.Ordinal);
        Assert.Contains("Route.Stages[i].Order", recipe, StringComparison.Ordinal);
        Assert.Contains("Deja el orden vacío para excluir un proceso", recipe, StringComparison.Ordinal);
        Assert.Contains("Guardar lista de materiales", recipe, StringComparison.Ordinal);
        Assert.Contains("Crear ruta de producción", recipe, StringComparison.Ordinal);
        Assert.Contains("Requiere etapa", recipe, StringComparison.Ordinal);
        Assert.DoesNotContain("/Admin/Production/Routes", recipe, StringComparison.Ordinal);
        Assert.Contains("data-cycle-plan data-production-recipe", recipe, StringComparison.Ordinal);
        Assert.Contains("data-lookup-url=\"@Url.Page(\"/Operations/Lookup\")\"", recipe, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"Recipe.Lines[i].MaterialProductId\" type=\"hidden\"", recipe, StringComparison.Ordinal);
        Assert.Contains("data-cycle-plan-optional", recipe, StringComparison.Ordinal);
        Assert.Contains("data-cycle-plan-camera", recipe, StringComparison.Ordinal);
        Assert.Contains("data-production-recipe-lines", recipe, StringComparison.Ordinal);
        Assert.Contains("data-production-recipe-add", recipe, StringComparison.Ordinal);
        Assert.Contains("data-production-recipe-remove", recipe, StringComparison.Ordinal);
        Assert.Contains("Agregar material", recipe, StringComparison.Ordinal);
        Assert.Contains("role=\"combobox\"", recipe, StringComparison.Ordinal);
        Assert.Contains("role=\"listbox\"", recipe, StringComparison.Ordinal);
        Assert.DoesNotContain("Recipe.ProductId", recipe, StringComparison.Ordinal);
        Assert.Contains("_ProductionRecipe", edit, StringComparison.Ordinal);
        Assert.Contains("/Pages/Operations/CycleCounts/_CameraScanner.cshtml", edit, StringComparison.Ordinal);
        Assert.Contains("zxing-browser.min.js", edit, StringComparison.Ordinal);
        Assert.Contains("~/js/cycle-count.js", edit, StringComparison.Ordinal);
        Assert.Contains("~/js/production-recipe.js", edit, StringComparison.Ordinal);
        Assert.Contains("OnPostRecipeAsync(Guid id", editModel, StringComparison.Ordinal);
        Assert.Contains("OnPostRouteAsync(Guid id", editModel, StringComparison.Ordinal);
        Assert.Contains("GroupBy(x => x.Order!.Value)", editModel, StringComparison.Ordinal);
        Assert.Contains("selected.OrderBy(x => x.Order)", editModel, StringComparison.Ordinal);
        Assert.Contains("new(id, Recipe.BaseQuantity", editModel, StringComparison.Ordinal);
        Assert.Contains("ModelState.Remove(\"Recipe.Pin\")", editModel, StringComparison.Ordinal);
        Assert.Contains("Cada línea iniciada requiere", editModel, StringComparison.Ordinal);
        Assert.Contains("ValidateOnly(Route, nameof(Route))", editModel, StringComparison.Ordinal);
        Assert.Contains("Guid? MaterialProductId", editModel, StringComparison.Ordinal);
        Assert.Contains("Guid? StageId", editModel, StringComparison.Ordinal);
        Assert.Contains("decimal? Quantity", editModel, StringComparison.Ordinal);
        Assert.Contains("Receta y materiales", details, StringComparison.Ordinal);
        Assert.Contains("Configurar receta", details, StringComparison.Ordinal);
        Assert.Contains("Editar receta", details, StringComparison.Ordinal);
        Assert.Contains("Guardar y configurar receta", create, StringComparison.Ordinal);
        Assert.Contains("name=\"nextStep\" value=\"details\"", create, StringComparison.Ordinal);
        Assert.Contains("name=\"nextStep\" value=\"production\"", create, StringComparison.Ordinal);
        Assert.Contains("string.Equals(nextStep, \"production\"", createModel, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", recipe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RedirectToPage(\"/Admin/Catalogs/Products/Index\")", redirect, StringComparison.Ordinal);
        var lookupScript = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count.js"));
        Assert.Contains("const fields = [];", lookupScript, StringComparison.Ordinal);
        Assert.Contains("fields.push(field);", lookupScript, StringComparison.Ordinal);
        Assert.Contains("cycle-plan:refresh", lookupScript, StringComparison.Ordinal);
        Assert.Contains("cyclePlanInitialized", lookupScript, StringComparison.Ordinal);
        Assert.Contains("field.optional || field.input.value.trim()", lookupScript, StringComparison.Ordinal);
        Assert.Contains("querySelectorAll(\"[data-cycle-plan]\")", lookupScript, StringComparison.Ordinal);
        var recipeScript = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js", "production-recipe.js"));
        Assert.Contains("[data-production-recipe-line]", recipeScript, StringComparison.Ordinal);
        Assert.Contains("const lines = () =>", recipeScript, StringComparison.Ordinal);
        Assert.Contains("cloneNode(true)", recipeScript, StringComparison.Ordinal);
        Assert.Contains("Recipe.Lines[${index}]", recipeScript, StringComparison.Ordinal);
        Assert.Contains("Material limpiado", recipeScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Selecciona el proceso donde se incorpora el material", recipeScript, StringComparison.Ordinal);
        Assert.Contains("Indica una cantidad mayor que cero", recipeScript, StringComparison.Ordinal);
        var search = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Trace.cshtml"));
        Assert.Contains("lote de materia prima", search, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void P5_execution_keeps_server_confirmation_accessibility_and_admin_reason_boundary()
    {
        var root = FindRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Execution.cshtml"));
        var handler = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Execution.cshtml.cs"));
        var reasons = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Production", "Reasons.cshtml.cs"));
        Assert.Contains("method=\"post\"", page);
        Assert.Contains("asp-for=\"Input.OperationId\"", page);
        Assert.Contains("asp-for=\"Input.Version\"", page);
        Assert.Contains("asp-for=\"Input.AdminPin\"", page);
        Assert.Contains("production-execution.js", page);
        Assert.DoesNotContain("onclick=", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Input.Pin = Input.AdminPin = \"\"", handler);
        Assert.Contains("Authorize(Policy = \"AdminOnly\")", reasons);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("No se encontró el repositorio.");
    }
}
