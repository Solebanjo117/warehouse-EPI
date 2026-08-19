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
        var rows = await reportService.ExportAsync(new(interval.FromInclusive, interval.ToExclusive,
            search?.Trim(), wipAreaId), token);
        var localNow = await clock.ConvertAsync(DateTimeOffset.UtcNow, token);
        var name = $"reporte-wip-{localNow:yyyyMMddHHmmss}";
        var headers = new[] { "Folio", "Fecha salida", "Producto", "Descripción", "Unidad", "Rack origen", "WIP",
            "Enviado", "Devuelto a bodega", "Devuelto a proveedor", "Consumo asumido", "Responsable", "Referencia", "Notas" };
        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("WIP");
            for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];
            var rowNumber = 2;
            foreach (var row in rows)
            {
                var values = Values(row); for (var column = 0; column < values.Length; column++)
                    sheet.Cell(rowNumber, column + 1).Value = Convert.ToString(values[column], CultureInfo.InvariantCulture);
                rowNumber++;
            }
            sheet.Columns().AdjustToContents(); using var output = new MemoryStream(); workbook.SaveAs(output);
            return File(output.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        }
        var csv = new StringBuilder().AppendJoin(',', headers.Select(Csv)).Append("\r\n");
        foreach (var row in rows) csv.AppendJoin(',', Values(row).Select(Csv)).Append("\r\n");
        return File(new UTF8Encoding(true).GetBytes(csv.ToString()), "text/csv; charset=utf-8", name + ".csv");
    }
    private static object?[] Values(WipIssueRow row) => [row.MovementId, row.OccurredAt.ToString("O"), row.ProductSku,
        row.ProductDescription, row.Unit, row.SourceLocation, row.WipArea, row.Issued, row.WarehouseReturned,
        row.SupplierReturned, row.AssumedConsumed, row.Responsible, row.Reference, row.Notes];
    private static string Csv(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("\"", "\"\"") + "\"";
}
