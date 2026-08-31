using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed class UnifiedTraceExportService(WarehouseSettingsService settingsService)
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@', '\t', '\r'];

    public async Task<byte[]> ToExcelAsync(IReadOnlyList<UnifiedTraceRow> rows, UnifiedTraceFilter filter, CancellationToken token = default)
    {
        var settings = await settingsService.GetAsync(token);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Trazabilidad");
        sheet.Cell(1, 1).SetValue(Safe($"{settings.WarehouseName} - Trazabilidad unificada"));
        sheet.Cell(1, 1).Style.Font.Bold = true; sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Cell(2, 1).SetValue(Safe($"Total: {rows.Count} eventos | Zona horaria: {settings.TimeZoneId} | Filtros: {Describe(filter)}"));
        string[] headers = ["Evento", "Fecha / Hora", "Categoría", "Título", "Resumen", "Estado", "Efectivo", "Responsable", "SKU", "Cantidad", "Unidad", "Ubicación", "Lote interno", "Lote/rollo externo", "Documento", "Movimiento"];
        for (var index = 0; index < headers.Length; index++) { var cell=sheet.Cell(4,index+1); cell.SetValue(headers[index]); cell.Style.Font.Bold=true; cell.Style.Font.FontColor=XLColor.White; cell.Style.Fill.BackgroundColor=XLColor.FromArgb(15,23,42); }
        var rowIndex = 5;
        foreach (var row in rows)
        {
            sheet.Cell(rowIndex,1).SetValue(Safe(row.Id));
            sheet.Cell(rowIndex,2).SetValue(TimeZoneInfo.ConvertTime(row.OccurredAt,zone).DateTime); sheet.Cell(rowIndex,2).Style.DateFormat.Format="yyyy-mm-dd hh:mm:ss";
            sheet.Cell(rowIndex,3).SetValue(Safe(row.Kind)); sheet.Cell(rowIndex,4).SetValue(Safe(row.Title)); sheet.Cell(rowIndex,5).SetValue(Safe(row.Summary)); sheet.Cell(rowIndex,6).SetValue(Safe(row.Status));
            sheet.Cell(rowIndex,7).SetValue(row.IsEffective is null ? "No aplica" : row.IsEffective.Value ? "Sí" : "No"); sheet.Cell(rowIndex,8).SetValue(Safe(row.Responsible)); sheet.Cell(rowIndex,9).SetValue(Safe(row.ProductSku));
            if(row.Quantity is not null){sheet.Cell(rowIndex,10).SetValue(row.Quantity.Value);sheet.Cell(rowIndex,10).Style.NumberFormat.Format="#,##0.0000";}
            sheet.Cell(rowIndex,11).SetValue(Safe(row.Unit));sheet.Cell(rowIndex,12).SetValue(Safe(row.Location));sheet.Cell(rowIndex,13).SetValue(Safe(row.InternalLot));sheet.Cell(rowIndex,14).SetValue(Safe(row.ExternalLot));sheet.Cell(rowIndex,15).SetValue(Safe(row.DocumentId?.ToString()));sheet.Cell(rowIndex,16).SetValue(Safe(row.MovementId?.ToString()));rowIndex++;
        }
        sheet.Range(4,1,Math.Max(4,rowIndex-1),headers.Length).SetAutoFilter(); sheet.SheetView.FreezeRows(4); sheet.Columns().AdjustToContents(4,Math.Max(4,rowIndex-1));
        using var stream=new MemoryStream();workbook.SaveAs(stream);return stream.ToArray();
    }

    public async Task<byte[]> ToCsvAsync(IReadOnlyList<UnifiedTraceRow> rows, UnifiedTraceFilter filter, CancellationToken token = default)
    {
        var settings=await settingsService.GetAsync(token);var zone=TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);var builder=new StringBuilder();
        builder.AppendLine("Evento,Fecha / Hora,Categoría,Título,Resumen,Estado,Efectivo,Responsable,SKU,Cantidad,Unidad,Ubicación,Lote interno,Lote/rollo externo,Documento,Movimiento,Zona horaria,Filtros aplicados");
        foreach(var row in rows){builder.Append(Csv(Safe(row.Id))).Append(',').Append(Csv(TimeZoneInfo.ConvertTime(row.OccurredAt,zone).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture))).Append(',').Append(Csv(Safe(row.Kind))).Append(',').Append(Csv(Safe(row.Title))).Append(',').Append(Csv(Safe(row.Summary))).Append(',').Append(Csv(Safe(row.Status))).Append(',').Append(Csv(row.IsEffective is null?"No aplica":row.IsEffective.Value?"Sí":"No")).Append(',').Append(Csv(Safe(row.Responsible))).Append(',').Append(Csv(Safe(row.ProductSku))).Append(',').Append(row.Quantity?.ToString("0.0000",CultureInfo.InvariantCulture)??string.Empty).Append(',').Append(Csv(Safe(row.Unit))).Append(',').Append(Csv(Safe(row.Location))).Append(',').Append(Csv(Safe(row.InternalLot))).Append(',').Append(Csv(Safe(row.ExternalLot))).Append(',').Append(Csv(Safe(row.DocumentId?.ToString()))).Append(',').Append(Csv(Safe(row.MovementId?.ToString()))).Append(',').Append(Csv(Safe(settings.TimeZoneId))).Append(',').Append(Csv(Safe(Describe(filter)))).AppendLine();}
        var encoding=new UTF8Encoding(true);return [..encoding.GetPreamble(),..encoding.GetBytes(builder.ToString())];
    }
    private static string Describe(UnifiedTraceFilter filter)=>$"Buscar={filter.Search??"Todos"}; Categoría={filter.Kind??"Todas"}; DesdeUTC={filter.FromUtc?.ToString("O")??"Sin límite"}; HastaUTC={filter.ToUtc?.ToString("O")??"Sin límite"}";
    private static string Safe(string? value){var text=value??string.Empty;return text.Length>0&&FormulaPrefixes.Contains(text[0])?"'"+text:text;}
    private static string Csv(string value)=>$"\"{value.Replace("\"","\"\"")}\"";
}
