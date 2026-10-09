namespace WarehouseEPI.Web.Navigation;

public sealed record ModuleAction(string Title, string Description, string Icon, string Page,
    bool AdminOnly = false, bool SupplyCount = false, string? View = null, bool SignedInOnly = false,
    string? Mode = null)
{
    public Dictionary<string, string> RouteValues
    {
        get
        {
            Dictionary<string, string> values = [];
            if (View is not null) values["view"] = View;
            if (Mode is not null) values["mode"] = Mode;
            return values;
        }
    }
}

public sealed record ModuleSection(string Title, IReadOnlyList<ModuleAction> Actions);
public sealed record NavigationModule(string Key, string Title, string Description, string Icon,
    bool AdminOnly, IReadOnlyList<ModuleSection> Sections);

public static class ModuleNavigation
{
    public static IReadOnlyList<NavigationModule> GetVisible(bool isAdmin) => GetVisible(isAdmin ? "ADMIN" : null);

    public static IReadOnlyList<NavigationModule> GetVisible(string? role) =>
        GetAll(role == "ADMIN").Select(module => module with
        {
            Title = module.Key == "catalogs" && role == "PRODUCTION" ? "Productos" : module.Title,
            Description = module.Key == "catalogs" && role == "PRODUCTION" ? "Consultar fichas, existencias y recetas." : module.Description,
            Sections = module.Sections.Select(section => section with
            {
                Title = section.Title == "Administración" && role == "PRODUCTION" ? "Consulta" : section.Title,
                Actions = section.Actions.Select(action => role == "PRODUCTION" && action.Page == "/Admin/Production/Schedule"
                    ? action with { Page = "/Production/Schedule", Description = "Consultar el programa semanal.", AdminOnly = false } : action)
                    .Where(action => (!action.SignedInOnly || role is not null) && WarehouseEPI.Web.Security.PageAccess.Allows(
                        WarehouseEPI.Web.Security.PageAccess.PolicyFor(action.Page), role)).ToArray()
            }).Where(section => section.Actions.Count > 0).ToArray()
        }).Where(module => module.Sections.Count > 0).ToArray();

    public static NavigationModule? Find(string key, bool isAdmin) =>
        GetAll(isAdmin).FirstOrDefault(module => module.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static NavigationModule? Active(string page, string? moduleKey, bool isAdmin) => Active(page, moduleKey, isAdmin ? "ADMIN" : null);

    public static NavigationModule? Active(string page, string? moduleKey, string? role)
    {
        var visible = GetVisible(role);
        if (page.Equals("/Modules/Index", StringComparison.OrdinalIgnoreCase))
            return visible.FirstOrDefault(module => module.Key.Equals(moduleKey, StringComparison.OrdinalIgnoreCase));

        // Longest segment match: Products never matches ProductTypes, and
        // Production never matches ProductionSupply by accident.
        return visible.SelectMany(module => module.Sections.SelectMany(section =>
                section.Actions.Select(action => (Module: module, Prefix: Prefix(action.Page)))))
            .Concat(ExtraMatches(visible))
            .Where(match => page.Equals(match.Prefix, StringComparison.OrdinalIgnoreCase) ||
                page.StartsWith(match.Prefix + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(match => match.Prefix.Length)
            .Select(match => match.Module).FirstOrDefault();
    }

    private static string Prefix(string page) => page.EndsWith("/Index", StringComparison.Ordinal)
        ? page[..^6] : page;

    private static IEnumerable<(NavigationModule Module, string Prefix)> ExtraMatches(
        IReadOnlyList<NavigationModule> modules)
    {
        foreach (var module in modules)
        {
            if (module.Key == "production")
                foreach (var prefix in new[] { "/Operations/ProductionSupply", "/Operations/WipReturn", "/Admin/Production" })
                    yield return (module, prefix);
            if (module.Key == "labels") yield return (module, "/Admin/Labels");
            if (module.Key == "reports") yield return (module, "/Reports/Executive");
            if (module.Key == "inventory")
                foreach (var prefix in new[] { "/Admin/Catalogs/Locations", "/Locations", "/Admin/Reports/Movements" })
                    yield return (module, prefix);
            if (module.Key == "administration") yield return (module, "/Admin/Settings");
        }
    }

    private static NavigationModule[] GetAll(bool isAdmin) =>
    [
        new("operations", "Operaciones", "Registra movimientos y verifica el inventario.", "entry", false,
        [
            new("Movimientos", [
                new("Entrada", "Recibir material en el almacén.", "entry", "/Operations/Entry"),
                new("Recibir en STAGING", "Registrar una entrada con destino STAGING", "entry", "/Operations/Entry", Mode: "staging"),
                new("Salida", "Retirar material o surtir WIP.", "exit", "/Operations/Exit"),
                new("Transferencia", "Mover material entre ubicaciones.", "transfer", "/Operations/Transfer"),
                new("Lista de staging", "Acomodar cada llegada de material.", "transfer", "/Operations/Staging/Index"),
                new("Ajuste", "Corregir existencias con trazabilidad.", "adjust", "/Operations/Adjustment")]),
            new("Verificación", [new("Conteos cíclicos", "Capturar y revisar conteos de inventario.", "inventory", "/Operations/CycleCounts/Index")])
        ]),
        new("production", "Producción", "Surte materiales y da seguimiento a la producción.", "movements", false,
        [
            new("Operación", [
                new("Tabla y balance", "Consultar y registrar la producción de cada día.", "dashboard", "/Operations/Production/Index")]),
            new("Administración", [
                new("Programa semanal", "Preparar, publicar e importar el programa lunes–domingo.", "products", "/Admin/Production/Schedule", true),
                new("Procesos", "Configurar los procesos de producción.", "adjust", "/Admin/Production/Processes", true),
                new("Turnos", "Registrar turnos para la producción diaria.", "adjust", "/Admin/Production/Routes", true)])
        ]),
        new("inventory", "Inventario", "Consulta existencias, ubicaciones y trazabilidad.", "inventory", false,
        [
            new("Consulta", [
                new("Notificaciones", "Consultar saldos negativos y productos bajo mínimo.", "alert", "/Reports/Notifications/Index", SignedInOnly: true),
                new("Existencias", "Buscar el saldo de productos por ubicación.", "inventory", "/Inventory/Index"),
                new("Ubicaciones", "Explorar ubicaciones y el croquis del almacén.", "location", isAdmin ? "/Admin/Catalogs/Locations/Index" : "/Locations/Index")]),
            new("Control de inventario", [
                new("Movimientos", "Consultar movimientos y sus fichas.", "movements", "/Admin/Inventory/Movements/Index", true),
                new("Lotes", "Consultar saldos y trazabilidad por lote.", "lots", "/Admin/Inventory/Lots/Index", true),
                new("Centro de excepciones", "Revisar condiciones que requieren atención.", "alert", "/Admin/Inventory/Alerts", true)])
        ]),
        new("labels", "Etiquetas", "Genera etiquetas y placas para la operación.", "label", false,
        [
            new("Impresión", [
                new("Generar etiquetas", "Imprimir usando formatos publicados.", "label", "/Operations/Labels/Index"),
                new("Placas de pallet", "Identificar saldo libre por ubicación e imprimir placas.", "label", "/Operations/PalletLabels/Index")]),
            new("Administración", [new("Diseñar formatos", "Diseñar y publicar formatos de etiquetas.", "label", "/Admin/Labels/Templates/Index", true)])
        ]),
        new("reports", "Reportes", "Revisa indicadores, saldos e historial del almacén.", "dashboard", false,
        [
            new("Resumen y actividad", [
                new(isAdmin ? "Resumen operativo" : "Tablero diario", "Consultar los indicadores de la operación.", "dashboard", "/Reports/Dashboard/Index"),
                new(isAdmin ? "Pendientes" : "Carga de trabajo", "Consultar el trabajo y la actividad del almacén.", "dashboard", "/Reports/Workload/Index", View: isAdmin ? "pending" : null)]),
            new("Análisis de inventario", [
                new("Analítica de inventario", "Analizar existencias y condiciones del inventario.", "dashboard", "/Reports/Inventory/Index"),
                new("Kardex de productos", "Consultar el historial de un producto.", "movements", "/Reports/Kardex/Index"),
                new("Reporte WIP", "Revisar el material en proceso.", "movements", "/Reports/Wip/Index")]),
            new("Producción", [
                new("Reportes de producción", "Analizar órdenes, materiales, retrabajo y registros.", "dashboard", "/Reports/Production/Index", true)])
        ]),
        new("catalogs", "Catálogos", "Administra los productos y su clasificación.", "products", true,
        [
            new("Productos", [
                new("Productos", "Consultar fichas, existencias y recetas.", "products", "/Admin/Catalogs/Products/Index", true),
                new("Tipos de producto", "Administrar los tipos del catálogo.", "product-types", "/Admin/Catalogs/ProductTypes/Index", true),
                new("Clases de producto", "Administrar las clases del catálogo.", "product-classes", "/Admin/Catalogs/ProductClasses/Index", true),
                new("Unidades de medida", "Administrar las unidades base.", "units", "/Admin/Catalogs/Units/Index", true)])
        ]),
        new("administration", "Administración", "Administra usuarios y configuración del sistema.", "system", true,
        [
            new("Sistema", [
                new("Usuarios", "Administrar usuarios y accesos.", "users", "/Admin/Users/Index", true),
                new("Estado del sistema", "Revisar el estado de la instalación.", "system", "/Admin/System/Index", true),
                new("Respaldos", "Crear y descargar un respaldo protegido.", "system", "/Admin/System/Backups", true),
                new("Datos del negocio", "Configurar la identidad del negocio y almacén.", "system", "/Admin/Settings/Business", true)])
        ])
    ];
}
