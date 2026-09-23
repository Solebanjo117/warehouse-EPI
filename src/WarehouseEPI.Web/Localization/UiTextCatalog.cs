namespace WarehouseEPI.Web.Localization;

internal static class UiTextCatalog
{
    public static Type ForModel(Type modelType)
    {
        var modelNamespace = modelType.Namespace ?? string.Empty;
        if (modelNamespace.Contains(".Pages.Operations.Production", StringComparison.Ordinal)
            || modelNamespace.Contains(".Pages.Admin.Production", StringComparison.Ordinal))
            return typeof(ProductionTexts);
        if (modelNamespace.Contains(".Pages.Operations", StringComparison.Ordinal))
            return typeof(OperationsTexts);
        if (modelNamespace.Contains(".Pages.Admin", StringComparison.Ordinal)
            || modelNamespace.Contains(".Pages.Inventory", StringComparison.Ordinal)
            || modelNamespace.Contains(".Pages.Locations", StringComparison.Ordinal)
            || modelNamespace.Contains(".Pages.Reports", StringComparison.Ordinal))
            return typeof(CatalogTexts);
        return typeof(SharedTexts);
    }
}
