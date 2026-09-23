using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace WarehouseEPI.Web.Localization;

public static class UiLanguage
{
    public const string CookieName = "WarehouseEPI.Language";

    public static bool IsSupported(string? language) => language is "es" or "en";

    public static RequestLocalizationOptions CreateOptions()
    {
        // Language preference must not change model binding, decimal input or dates.
        var operationalCulture = CultureInfo.CurrentCulture;
        var options = new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(operationalCulture, CultureInfo.GetCultureInfo("es")),
            SupportedCultures = [operationalCulture],
            SupportedUICultures = [CultureInfo.GetCultureInfo("es"), CultureInfo.GetCultureInfo("en")],
            ApplyCurrentCultureToResponseHeaders = true
        };
        options.RequestCultureProviders.Clear();
        options.RequestCultureProviders.Add(new CustomRequestCultureProvider(context =>
        {
            var language = context.Request.Cookies[CookieName];
            ProviderCultureResult? result = IsSupported(language)
                ? new ProviderCultureResult(operationalCulture.Name, language!)
                : null;
            return Task.FromResult(result);
        }));
        return options;
    }
}
