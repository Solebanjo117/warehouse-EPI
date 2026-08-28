using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Reports.Wip;

[Authorize(Policy = "AdminOnly")]
public sealed class ExportModel(WipReportService reportService, WarehouseClock clock) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string format, DateOnly? from, DateOnly? to, string? search,
        Guid? wipAreaId, CancellationToken token)
    {
        var interval = await clock.GetUtcIntervalAsync(from, to, token);
        var report = await reportService.GetTrackedPageAsync(new(interval.FromInclusive, interval.ToExclusive,
            search?.Trim(), wipAreaId), 1, 10_001, token);
        var rows = report.Inventory.Select(row => new ExportRow(
                "Inventario actual", row.UpdatedAt, null, row.ProductSku, row.ProductDescription, row.Unit,
                row.WipArea, null, null, row.Quantity, null, row.OldestPositiveLotDate, null, null))
            .Concat(report.Activity.Select(row => new ExportRow(
                "Actividad", row.OccurredAt, row.MovementId, row.ProductSku, row.ProductDescription, row.Unit,
                row.WipArea, row.Category, Route(row.SourceLocation, row.DestinationLocation), row.Delta,
                row.Responsible, null, row.Reference, row.Notes)))
            .Take(10_001)
            .ToArray();
        if (report.TotalActivityCount + report.Inventory.Count > 10_000)
            return BadRequest("La exportación excede el límite estricto de 10,000 filas. Reduce el periodo o agrega filtros.");
        var localNow = await clock.ConvertAsync(DateTimeOffset.UtcNow, token);
        var name = $"reporte-wip-{localNow:yyyyMMddHHmmss}";
        var headers = new[] { "Población", "Fecha local", "Folio", "Producto", "Descripción", "Unidad", "WIP",
            "Clasificación", "Trayecto", "Cantidad", "Responsable", "Lote positivo más antiguo", "Referencia", "Notas" };
        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("WIP");
            for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];
            var rowNumber = 2;
            foreach (var row in rows)
            {
                var values = Values(row); for (var column = 0; column < values.Length; column++)
                {
                    var cell = sheet.Cell(rowNumber, column + 1);
                    if (values[column] is decimal quantity) cell.Value = quantity;
                    else if (values[column] is DateTimeOffset date) cell.Value = date.DateTime;
                    else if (values[column] is DateOnly lotDate) cell.Value = lotDate.ToDateTime(TimeOnly.MinValue);
                    else cell.Value = SafeText(Convert.ToString(values[column], CultureInfo.InvariantCulture));
                }
                rowNumber++;
            }
            sheet.Columns().AdjustToContents(); using var output = new MemoryStream(); workbook.SaveAs(output);
            return File(output.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        }
        var csv = new StringBuilder().AppendJoin(',', headers.Select(Csv)).Append("\r\n");
        foreach (var row in rows) csv.AppendJoin(',', Values(row).Select(Csv)).Append("\r\n");
        return File(new UTF8Encoding(true).GetBytes(csv.ToString()), "text/csv; charset=utf-8", name + ".csv");
    }
    private static object?[] Values(ExportRow row) => [row.Population, row.OccurredAt, row.MovementId, row.ProductSku,
        row.ProductDescription, row.Unit, row.WipArea, row.Category, row.Route, row.Quantity, row.Responsible,
        row.OldestPositiveLotDate, row.Reference, row.Notes];
    private static string Route(string? source, string? destination) => $"{source ?? "Exterior"} → {destination ?? "Exterior"}";
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
