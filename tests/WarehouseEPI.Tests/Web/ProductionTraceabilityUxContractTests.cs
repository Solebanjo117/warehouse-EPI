namespace WarehouseEPI.Tests.Web;

public sealed class ProductionTraceabilityUxContractTests
{
    [Fact]
    public void Traceability_pages_keep_visual_and_operational_contracts()
    {
        var root = FindRoot();
        var trace = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "_Traceability.cshtml"));
        var recipe = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Admin", "Catalogs", "Products", "_ProductionRecipe.cshtml"));
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
        Assert.Contains("Ajustar previsto", trace);
        Assert.Contains("ReverseResult", trace);
        Assert.DoesNotContain("onchange=", trace, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", trace, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ruta y receta", recipe);
        Assert.Contains("data-cycle-plan data-production-recipe data-lookup-url=\"@Url.Page(\"/Operations/Lookup\")\"", recipe, StringComparison.Ordinal);
        Assert.Contains("asp-for=\"Recipe.Lines[i].MaterialProductId\" type=\"hidden\"", recipe, StringComparison.Ordinal);
        Assert.Contains("data-cycle-plan-optional", recipe, StringComparison.Ordinal);
        Assert.Contains("data-cycle-plan-camera", recipe, StringComparison.Ordinal);
        Assert.Contains("role=\"combobox\"", recipe, StringComparison.Ordinal);
        Assert.Contains("role=\"listbox\"", recipe, StringComparison.Ordinal);
        Assert.DoesNotContain("Recipe.ProductId", recipe, StringComparison.Ordinal);
        Assert.Contains("_ProductionRecipe", edit, StringComparison.Ordinal);
        Assert.Contains("/Pages/Operations/CycleCounts/_CameraScanner.cshtml", edit, StringComparison.Ordinal);
        Assert.Contains("zxing-browser.min.js", edit, StringComparison.Ordinal);
        Assert.Contains("~/js/cycle-count.js", edit, StringComparison.Ordinal);
        Assert.Contains("~/js/production-recipe.js", edit, StringComparison.Ordinal);
        Assert.Contains("OnPostRecipeAsync(Guid id", editModel, StringComparison.Ordinal);
        Assert.Contains("new(id, Recipe.BaseQuantity", editModel, StringComparison.Ordinal);
        Assert.Contains("ModelState.Remove(\"Recipe.Pin\")", editModel, StringComparison.Ordinal);
        Assert.Contains("Cada material utilizado requiere", editModel, StringComparison.Ordinal);
        Assert.Contains("Guid? MaterialProductId", editModel, StringComparison.Ordinal);
        Assert.Contains("Guid? StageId", editModel, StringComparison.Ordinal);
        Assert.Contains("decimal? Quantity", editModel, StringComparison.Ordinal);
        Assert.Contains("Configuración de producción", details, StringComparison.Ordinal);
        Assert.Contains("Editar configuración de producción", details, StringComparison.Ordinal);
        Assert.Contains("RedirectToPage(\"/Admin/Catalogs/Products/Index\")", redirect, StringComparison.Ordinal);
        var lookupScript = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js", "cycle-count.js"));
        Assert.Contains("const fields = [];", lookupScript, StringComparison.Ordinal);
        Assert.Contains("fields.push(field);", lookupScript, StringComparison.Ordinal);
        Assert.Contains("field.optional || field.input.value.trim()", lookupScript, StringComparison.Ordinal);
        Assert.Contains("querySelectorAll(\"[data-cycle-plan]\")", lookupScript, StringComparison.Ordinal);
        var recipeScript = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "wwwroot", "js", "production-recipe.js"));
        Assert.Contains("[data-production-recipe-line]", recipeScript, StringComparison.Ordinal);
        Assert.Contains("Selecciona el proceso donde se incorpora el material", recipeScript, StringComparison.Ordinal);
        Assert.Contains("Indica una cantidad mayor que cero", recipeScript, StringComparison.Ordinal);
        var search = File.ReadAllText(Path.Combine(root, "src", "WarehouseEPI.Web", "Pages", "Operations", "Production", "Trace.cshtml"));
        Assert.Contains("lote de materia prima", search, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WarehouseEPI.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("No se encontró el repositorio.");
    }
}
