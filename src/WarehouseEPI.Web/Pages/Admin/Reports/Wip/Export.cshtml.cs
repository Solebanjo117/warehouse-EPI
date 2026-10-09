using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;
using WarehouseEPI.Web.Localization;
using WarehouseEPI.Web.Reporting;

namespace WarehouseEPI.Web.Pages.Admin.Reports.Wip;

[Authorize(Policy = "AdminOnly")]
public sealed class ExportModel(WipReportService reportService, WarehouseClock clock, IStringLocalizer<CatalogTexts> localizer,
    WarehouseSettingsService settingsService, TimeProvider timeProvider, WarehouseDbContext db) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string format, DateOnly? from, DateOnly? to, string? search,
        Guid? wipAreaId, string? attention = null, string? sort = null, CancellationToken token = default)
    {
        var interval = await clock.GetUtcIntervalAsync(from, to, token);
        var now = timeProvider.GetUtcNow();
        var settings = await settingsService.GetAsync(token);
        var selection = WipReportSelection.FromQuery(attention, sort);
        var filter = selection.Apply(
            new(interval.FromInclusive, interval.ToExclusive, search?.Trim(), wipAreaId), now, settings.WipReminderDays);
        var report = await reportService.GetTrackedPageAsync(filter, 1, 10_001, token);
        var rows = report.Inventory.ToArray();
        if (report.TotalActivityCount > 10_000)
            return BadRequest(localizer["La exportación excede el límite estricto de 10,000 filas. Reduce el periodo o agrega filtros."]);
        var localNow = await clock.ConvertAsync(now, token);
        var name = $"reporte-wip-{localNow:yyyyMMddHHmmss}";
        var headers = new[] { "Documento", "Fecha local", "Producto", "Descripción", "Unidad", "WIP", "Origen documental",
            "Entregado", "Uso registrado", "Merma", "A bodega", "A proveedor", "Pendiente documental", "Responsable" };
        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            var area = wipAreaId.HasValue
                ? await db.Locations.AsNoTracking().Where(x => x.Id == wipAreaId.Value).Select(x => x.Code).SingleOrDefaultAsync(token)
                    ?? $"Área no encontrada ({wipAreaId})"
                : "Todas las áreas";
            var context = new WipExcelContext(settings.WarehouseName, localNow, settings.TimeZoneId,
                from, to, filter.Search, area, selection.Attention switch
                {
                    "pending" => "Documentos pendientes",
                    "aged" => $"Pendientes antiguos — {settings.WipReminderDays} días o más",
                    _ => "Todos los documentos"
                }, selection.Sort == "oldest" ? "Más antiguas" : "Más recientes");
            return File(WipExcelExporter.Export(rows, context), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        }
        var csv = new StringBuilder().AppendJoin(',', headers.Select(Csv)).Append("\r\n");
        foreach (var row in rows) csv.AppendJoin(',', Values(row).Select(Csv)).Append("\r\n");
        return File(new UTF8Encoding(true).GetBytes(csv.ToString()), "text/csv; charset=utf-8", name + ".csv");
    }
    private static object?[] Values(WipInventoryRow row) => [row.DocumentId, row.UpdatedAt, row.ProductSku,
        row.ProductDescription, row.Unit, row.WipArea, row.IsOpening ? "Apertura del corte" : "Surtimiento",
        row.Delivered, row.Used, row.Scrapped, row.WarehouseReturned, row.SupplierReturned, row.Quantity, row.Responsible];
    private static string SafeText(string? value)
    {
        var text = value ?? string.Empty;
        return text.Length > 0 && text[0] is '=' or '+' or '-' or '@' ? "'" + text : text;
    }
    private static string Csv(object? value) => "\"" + SafeText(Convert.ToString(value, CultureInfo.InvariantCulture)).Replace("\"", "\"\"") + "\"";
    private sealed record ExportRow(string Population, DateTimeOffset OccurredAt, Guid? MovementId,
        string ProductSku, string? ProductDescription, string Unit, string WipArea, string? Category,
        string? Route, decimal Quantity, string? Responsible, DateOnly? OldestPositiveLotDate,
        string? Reference, string? Notes);
}
