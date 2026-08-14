using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Movements;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(InventoryHistoryService history) : PageModel
{
    private const int PageSize = 50;
    public InventoryMovementHistoryPage Results { get; private set; } = new([], 0);
    public int PageNumber { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Results.TotalCount / (double)PageSize));
    public string? Search { get; private set; }
    public DateTimeOffset? From { get; private set; }
    public DateTimeOffset? To { get; private set; }
    public InventoryMovementType? Type { get; private set; }

    public async Task OnGetAsync(string? search, DateTimeOffset? from, DateTimeOffset? to, InventoryMovementType? type, int pageNumber = 1, CancellationToken token = default)
    {
        Search = search?.Trim(); From = from; To = to; Type = type; PageNumber = Math.Max(1, pageNumber);
        Results = await history.SearchAsync(new(From, To, Type, Search, null, null, null), PageNumber, PageSize, token);
        PageNumber = Math.Min(PageNumber, TotalPages);
    }

    public async Task<IActionResult> OnGetExportAsync(string format, string? search, DateTimeOffset? from, DateTimeOffset? to, InventoryMovementType? type, CancellationToken token)
    {
        var rows = await history.ExportAsync(new(from, to, type, search?.Trim(), null, null, null), token);
        var name = $"movimientos-{DateTime.UtcNow:yyyyMMddHHmmss}";
        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("Movimientos");
            sheet.Cell(1, 1).InsertData(new[] { new[] { "Movimiento", "Operación", "Tipo", "Fecha UTC", "Responsable", "Referencia", "Producto" } });
            var row = 2; foreach (var item in rows) { sheet.Cell(row, 1).Value = item.Id.ToString(); sheet.Cell(row, 2).Value = item.OperationId.ToString(); sheet.Cell(row, 3).Value = item.Type.ToString(); sheet.Cell(row, 4).Value = item.OccurredAt.UtcDateTime; sheet.Cell(row, 5).Value = item.ResponsibleName; sheet.Cell(row, 6).Value = item.Reference; sheet.Cell(row++, 7).Value = item.ProductSummary; }
            sheet.Columns().AdjustToContents(); using var output = new MemoryStream(); workbook.SaveAs(output);
            return File(output.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        }
        var csv = new StringBuilder("Movimiento,Operacion,Tipo,Fecha UTC,Responsable,Referencia,Producto\r\n");
        foreach (var item in rows) csv.Append(Csv(item.Id)).Append(',').Append(Csv(item.OperationId)).Append(',').Append(Csv(item.Type)).Append(',').Append(Csv(item.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))).Append(',').Append(Csv(item.ResponsibleName)).Append(',').Append(Csv(item.Reference)).Append(',').Append(Csv(item.ProductSummary)).Append("\r\n");
        return File(new UTF8Encoding(true).GetBytes(csv.ToString()), "text/csv; charset=utf-8", name + ".csv");
    }
    private static string Csv(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("\"", "\"\"") + "\"";
}
