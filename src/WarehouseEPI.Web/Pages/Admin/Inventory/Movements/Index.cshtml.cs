using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Web.Pages.Admin.Inventory.Movements;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(InventoryHistoryService history, WarehouseClock clock) : PageModel
{
    private const int PageSize = 25;
    public InventoryMovementHistoryPage Results { get; private set; } = new([], 0);
    public int PageNumber { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Results.TotalCount / (double)PageSize));
    public string? Search { get; private set; }
    public DateOnly? From { get; private set; }
    public DateOnly? To { get; private set; }
    public InventoryMovementType? Type { get; private set; }
    public InventoryHistoryCorrectionState State { get; private set; }
    public string Period { get; private set; } = "30";

    public async Task OnGetAsync(string? search, DateOnly? from, DateOnly? to, InventoryMovementType? type, InventoryHistoryCorrectionState state = InventoryHistoryCorrectionState.All, string? period = "30", int pageNumber = 1, CancellationToken token = default)
    {
        Period = period ?? "30"; var today = await clock.GetDateAsync(DateTimeOffset.UtcNow, token);
        if (from is null && to is null && Period != "all") { var days = Period == "today" ? 0 : Period == "7" ? 6 : 29; from = today.AddDays(-days); to = today; }
        Search = search?.Trim(); From = from; To = to; Type = type; State = state; PageNumber = Math.Max(1, pageNumber);
        var interval = await clock.GetUtcIntervalAsync(From, To, token);
        Results = await history.SearchAsync(new(interval.FromInclusive, interval.ToExclusive, Type, Search, null, null, null, State), PageNumber, PageSize, token);
        PageNumber = Math.Min(PageNumber, TotalPages);
    }

    public async Task<IActionResult> OnGetExportAsync(string format, string? search, DateOnly? from, DateOnly? to, InventoryMovementType? type, InventoryHistoryCorrectionState state = InventoryHistoryCorrectionState.All, CancellationToken token = default)
    {
        var interval = await clock.GetUtcIntervalAsync(from, to, token); var local = await clock.ConvertAsync(DateTimeOffset.UtcNow, token);
        var settings = await HttpContext.RequestServices.GetRequiredService<WarehouseSettingsService>().GetAsync(token);
        var rows = await history.ExportTraceAsync(new(interval.FromInclusive, interval.ToExclusive, type, search?.Trim(), null, null, null, state), settings.TimeZoneId, token);
        var name = $"movimientos-{local:yyyyMMddHHmmss}";
        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("Movimientos");
            sheet.Cell(1, 1).InsertData(new[] { new[] { "Movimiento", "Operación", "Estado", "Fecha", "Zona", "Responsable", "Referencia", "Notas", "SKU", "Producto", "Unidad", "Cantidad", "Origen", "Destino", "Ubicación", "Lote histórico", "Fecha lote", "Asignación", "Saldo anterior", "Delta", "Saldo resultante" } });
            var row = 2; foreach (var item in rows) { var data = new object?[] { item.MovementId, item.OperationId, item.Status, item.OccurredAt.ToString("O"), item.TimeZoneId, item.Responsible, item.Reference, item.Notes, item.ProductSku, item.ProductDescription, item.Unit, item.CapturedQuantity, item.Source, item.Destination, item.Location, item.LotNumber, item.LotDate?.ToString("yyyy-MM-dd"), item.AllocationMode, item.Previous, item.Delta, item.Resulting }; for (var i = 0; i < data.Length; i++) sheet.Cell(row, i + 1).Value = Convert.ToString(data[i], CultureInfo.InvariantCulture); row++; }
            sheet.Columns().AdjustToContents(); using var output = new MemoryStream(); workbook.SaveAs(output);
            return File(output.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        }
        var csv = new StringBuilder("Movimiento,Operacion,Estado,Fecha,Zona,Responsable,Referencia,Notas,SKU,Producto,Unidad,Cantidad,Origen,Destino,Ubicacion,Lote historico,Fecha lote,Asignacion,Saldo anterior,Delta,Saldo resultante\r\n");
        foreach (var item in rows) csv.AppendJoin(',', new object?[] { item.MovementId, item.OperationId, item.Status, item.OccurredAt.ToString("O", CultureInfo.InvariantCulture), item.TimeZoneId, item.Responsible, item.Reference, item.Notes, item.ProductSku, item.ProductDescription, item.Unit, item.CapturedQuantity, item.Source, item.Destination, item.Location, item.LotNumber, item.LotDate, item.AllocationMode, item.Previous, item.Delta, item.Resulting }.Select(Csv)).Append("\r\n");
        return File(new UTF8Encoding(true).GetBytes(csv.ToString()), "text/csv; charset=utf-8", name + ".csv");
    }
    private static string Csv(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("\"", "\"\"") + "\"";
}
