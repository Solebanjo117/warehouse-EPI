using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Production;
using WarehouseEPI.Web.Localization;

namespace WarehouseEPI.Web.Production;

// Adapt legacy service messages at the UI boundary; arguments are never resource keys.
public static class ProductionDailyText
{
    public static string ImportSource(IStringLocalizer<ProductionTexts> texts, ProductionScheduleImportIssue issue) =>
        issue.Row is null && issue.Sheet == "Configuración" ? texts["Configuración diaria"] :
        issue.Row is null && issue.Sheet == "Archivo" ? texts["Archivo XLSX"] : issue.Sheet;

    private static readonly string[] Keys = [
        "Apertura pendiente de conciliación: {0}.",
        "Selecciona una tabla de cierre válida.",
        "El cierre no tiene las columnas de pendientes requeridas.",
        "La resolución de apertura contiene un producto inválido o repetido.",
        "La revisión cambió o no está disponible. Recarga y vuelve a revisar antes de continuar.",
        "La revisión cambió. Vuelve a validar antes de confirmar.",
        "Resuelve las discrepancias antes de confirmar.",
        "Este archivo ya fue importado; no se reemplazarán las semanas existentes.",
        "Ya existe una de las semanas del archivo. La carga inicial no reemplaza datos existentes.",
        "La importación coincidió con datos creados en otra sesión. Recarga la previsualización.",
        "Semana creada en borrador.",
        "Línea guardada con revisión automática.",
        "Semana publicada; órdenes, lotes y surtimientos fueron creados.",
        "Semana cerrada.",
        "Semana reabierta con auditoría.",
        "Áreas y turnos del programa diario actualizados.",
        "Los datos cambiaron. Recarga antes de continuar.",
        "NIP ADMIN inválido.",
        "La operación ya se utilizó con otros datos.",
        "No fue posible completar la operación.",
        "Producción registrada; el balance y el siguiente proceso se actualizaron.",
        "No fue posible registrar la captura.",
        "Captura revertida con trazabilidad. Registra una nueva captura si corresponde.",
        "No fue posible revertir la captura.",
        "Selecciona un archivo XLSX de hasta 15 MB.",
        "La previsualización expiró. Selecciona el archivo nuevamente.",
        "Histórico importado como solo lectura y semana actual creada en borrador con arrastre de apertura.",
        "No fue posible confirmar la importación.",
        "El borrador ya no existe.",
        "Las importaciones confirmadas se conservan como registro y no se pueden eliminar.",
        "Indica una cantidad positiva con hasta cuatro decimales.",
        "La fecha efectiva no puede estar en el futuro.",
        "El domingo no admite captura ordinaria.",
        "El área seleccionada no está configurada.",
        "Selecciona T1 o T2 configurado y activo.",
        "Selecciona un SKU activo del catálogo.",
        "La unidad del SKU no admite decimales.",
        "La fecha debe pertenecer a una semana abierta.",
        "no tiene lote único.",
        "la orden ya no está disponible.",
        "la orden ya no existe.",
        "NIP inválido.",
        "Indica el motivo del reverso.",
        "Una asignación ya no está disponible para reverso.",
        "La programación o una orden cambió. Se actualizó la previsualización sin duplicar resultados.",
        "no fue posible registrar el resultado.",
        "no fue posible completar el traspaso.",
        "La configuración requiere un usuario ADMIN autenticado.",
        "Selecciona tres procesos activos y diferentes.",
        "Selecciona T1 y T2 entre los turnos activos.",
        "La programación requiere un usuario ADMIN autenticado.",
        "La semana debe iniciar en lunes.",
        "Ya existe una programación para esa semana.",
        "Reabre la semana antes de modificarla.",
        "La fecha debe ser un lunes a sábado de la semana seleccionada.",
        "La cantidad debe ser positiva y admitir hasta cuatro decimales.",
        "La unidad del SKU no permite decimales.",
        "El SKU ya tiene movimientos. Corrige la orden desde Producción avanzada.",
        "El NIP ADMIN no corresponde a la sesión actual.",
        "Sólo una semana en borrador puede publicarse.",
        "Agrega al menos una línea antes de publicar.",
        "La operación requiere ADMIN.",
        "Configura Corte, Costura, Ready to Pack, T1 y T2 antes de publicar.",
        "no tiene ruta activa.",
        "la ruta contiene procesos fuera del módulo diario; usa Producción avanzada.",
        "el orden de Corte, Costura y Ready to Pack no es válido.",
        "el proceso inicial del arrastre no pertenece a la ruta.",
        "no tiene receta activa completa.",
        "la receta contiene materiales inactivos o procesos incompatibles.",
        "Configura las tres áreas y T1/T2 antes de importar.",
        "No se encontró una tabla de programa con encabezados Day, Part Number y Qty.",
        "El archivo no es un XLSX válido o está dañado.",
        "La importación requiere ADMIN.",
        "La fecha del programa no corresponde a lunes-sábado de la hoja.",
        "La cantidad programada debe ser positiva.",
        "La fecha de ejecución no corresponde a lunes-sábado de la hoja.",
        "Las piezas completadas deben ser positivas.",
        "Usa solo punto decimal, sin miles, con hasta cuatro decimales (ejemplo: 12.5).",
        "Faltan {0} piezas programadas disponibles para repartir por FIFO.",
        "faltan {0} de {1} surtido al proceso. Abre Surtimientos.",
        "La cantidad no puede ser menor que lo ya procesado ({0}).",
        "Falta la semana {0} esperada para la carga inicial.",
        "SKU sin resolver: {0}.",
        "Área sin resolver: {0}.",
        "Turno sin resolver: {0}.",
        "SKU de arrastre sin resolver: {0}.",
        "Línea {0}",
        "La semana debe estar en estado {0}.",
        "Selecciona una fecha válida.",
        "Este campo es obligatorio.",
        "Usa un NIP de 4 a 8 dígitos.",
        "Usa como máximo {1} caracteres.",
        "Revisa el valor introducido."
    ];
    private static readonly HashSet<string> StaticKeys = Keys.Where(x => !x.Contains('{')).ToHashSet(StringComparer.Ordinal);
    private static readonly (string Key, Regex Pattern)[] Templates = Keys.Where(x => x.Contains('{') && x != "Línea {0}")
        .Select(key => (key, new Regex("^" + Regex.Replace(Regex.Escape(key), @"\\\{([0-9]+)}", match => "(?<arg" + match.Groups[1].Value + ">.+?)") + "$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))))
        .ToArray();

    public static string WeekStatus(IStringLocalizer<ProductionTexts> texts, ProductionScheduleWeekStatus status) => texts[status switch
    {
        ProductionScheduleWeekStatus.Draft => "Borrador",
        ProductionScheduleWeekStatus.Open => "Abierta",
        _ => "Cerrada"
    }];

    public static string Message(IStringLocalizer<ProductionTexts> texts, string message)
    {
        if (message == "no fue posible registrar el resultado.") return texts["No fue posible registrar el resultado."];
        if (message == "no fue posible completar la operación.") return texts["No fue posible completar la operación."];
        if (StaticKeys.Contains(message)) return texts[message];
        foreach (var (key, pattern) in Templates)
        {
            var match = pattern.Match(message);
            if (match.Success)
            {
                var indices = Regex.Matches(key, @"\{(\d+)}").Select(x => int.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var args = Enumerable.Range(0, indices.Max() + 1).Select(index => (object)match.Groups["arg" + index].Value).ToArray();
                if (key == "La semana debe estar en estado {0}." && Enum.TryParse<ProductionScheduleWeekStatus>((string)args[0], out var status))
                    args[0] = WeekStatus(texts, status);
                return texts[key, args];
            }
        }
        var separator = message.IndexOf(": ", StringComparison.Ordinal);
        if (separator > 0)
        {
            var prefix = message[..separator];
            var line = Regex.Match(prefix, @"^Línea (\d+)(.*)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (line.Success) prefix = texts["Línea {0}", line.Groups[1].Value] + line.Groups[2].Value;
            return prefix + ": " + Message(texts, message[(separator + 2)..]);
        }
        // Messages from the advanced engine remain owned by that engine.
        return texts[message].Value;
    }

    public static void LocalizeErrors(ModelStateDictionary state, IStringLocalizer<ProductionTexts> texts)
    {
        foreach (var entry in state.Values)
        {
            var errors = entry.Errors.Select(error => string.IsNullOrEmpty(error.ErrorMessage)
                ? texts["Revisa el valor introducido."].Value : Message(texts, error.ErrorMessage)).ToArray();
            entry.Errors.Clear();
            foreach (var error in errors) entry.Errors.Add(error);
        }
    }
}

