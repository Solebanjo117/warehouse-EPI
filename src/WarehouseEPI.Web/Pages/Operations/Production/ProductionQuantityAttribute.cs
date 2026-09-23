using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WarehouseEPI.Web.Pages.Operations.Production;

// Scoped to production capture; never changes the application's culture.
public sealed class ProductionQuantityAttribute : ModelBinderAttribute
{
    public ProductionQuantityAttribute() : base(typeof(ProductionQuantityBinder)) { }
}

public sealed class ProductionQuantityBinder : IModelBinder
{
    public const string Error = "Usa solo punto decimal, sin miles, con hasta cuatro decimales (ejemplo: 12.5).";
    public static bool TryParse(string text, out decimal value)
    {
        value = 0;
        return Regex.IsMatch(text, @"^\d+(?:\.\d{1,4})?$") &&
            decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
    public Task BindModelAsync(ModelBindingContext context)
    {
        var input = context.ValueProvider.GetValue(context.ModelName);
        if (input == ValueProviderResult.None) return Task.CompletedTask;
        context.ModelState.SetModelValue(context.ModelName, input);
        if (input.Values.Count == 1 && TryParse(input.FirstValue ?? "", out var value))
            context.Result = ModelBindingResult.Success(value);
        else context.ModelState.TryAddModelError(context.ModelName, Error);
        return Task.CompletedTask;
    }
}

public static class ProductionCapture
{
    public static bool ValidateOnly(PageModel page, string prefix)
    {
        foreach (var key in page.ModelState.Keys.Where(key => key != prefix && !key.StartsWith(prefix + ".", StringComparison.Ordinal)).ToArray())
            page.ModelState.Remove(key);
        foreach (var item in page.ModelState.Where(x => x.Key.Contains(".Pallets.", StringComparison.Ordinal) &&
            (x.Key.EndsWith(".Quantity", StringComparison.Ordinal) || x.Key.Contains(".PalletQuantities[", StringComparison.Ordinal))).ToArray())
            if (item.Value?.AttemptedValue is string text && !ProductionQuantityBinder.TryParse(text, out _))
                page.ModelState.TryAddModelError(item.Key, ProductionQuantityBinder.Error);
        return page.ModelState.IsValid;
    }
    public static void ClearPins(PageModel page)
    {
        foreach (var key in page.ModelState.Keys.Where(key => key.EndsWith("Pin", StringComparison.OrdinalIgnoreCase)).ToArray())
            page.ModelState.Remove(key);
    }
    public static string Value(PageModel page, string key, decimal fallback) =>
        page.ModelState.TryGetValue(key, out var state) && state.AttemptedValue is not null
            ? state.AttemptedValue : fallback.ToString("0.####", CultureInfo.InvariantCulture);
}
