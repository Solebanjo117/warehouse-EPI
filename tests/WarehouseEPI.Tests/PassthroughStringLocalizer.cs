using Microsoft.Extensions.Localization;

namespace WarehouseEPI.Tests;

internal sealed class PassthroughStringLocalizer<T> : IStringLocalizer<T>
{
    public LocalizedString this[string name] => new(name, name);

    public LocalizedString this[string name, params object[] arguments] =>
        new(name, string.Format(System.Globalization.CultureInfo.CurrentCulture, name, arguments));

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}
