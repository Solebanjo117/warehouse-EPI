using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Locations;

// Translate known service messages only at the presentation boundary.
public static class WarehouseMapText
{
    private static readonly string[] Keys =
    [
        "El estilo arquitectónico de {0} no pertenece al catálogo permitido: trazo '{1}', relleno '{2}', grosor {3}.",
        "El pasillo mide {0} de ancho; 32 in es una referencia editorial, no normativa."
    ];
    private static readonly (string Key, Regex Pattern)[] Templates = Keys.Select(key =>
        (key, new Regex("^" + Regex.Replace(Regex.Escape(key), @"\\\{([0-9]+)}",
            match => "(?<arg" + match.Groups[1].Value + ">.+?)") + "$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))).ToArray();

    public static string Message(IStringLocalizer<CatalogTexts> texts, string message)
    {
        var direct = texts[message];
        if (!direct.ResourceNotFound) return direct.Value;
        foreach (var (key, pattern) in Templates)
        {
            var match = pattern.Match(message);
            if (!match.Success) continue;
            var count = Regex.Matches(key, @"\{(\d+)}")
                .Max(value => int.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
            var args = Enumerable.Range(0, count).Select(index => (object)match.Groups["arg" + index].Value).ToArray();
            return texts[key, args].Value;
        }
        return message;
    }
}
