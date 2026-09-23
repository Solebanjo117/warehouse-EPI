using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed class ProductionReportExportService
{
    public const int RowLimit = 10_000;

    public static byte[] ToExcel(string view, ProductionReportPage report, string filters)
    {
        EnsureLimit(report.TotalCount);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Producción");
        sheet.Cell(1, 1).Value = Safe("Reporte de producción");
        sheet.Cell(2, 1).Value = Safe($"Generado: {report.GeneratedAtLocal:yyyy-MM-dd HH:mm:ss} ({report.TimeZoneId})");
        sheet.Cell(3, 1).Value = Safe($"Filtros: {filters}");
        var rows = BuildRows(view, report);
        WriteExcel(sheet, rows.Headers, rows.Values);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static byte[] ToCsv(string view, ProductionReportPage report, string filters)
    {
        EnsureLimit(report.TotalCount);
        var rows = BuildRows(view, report);
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', rows.Headers.Select(Csv)));
        foreach (var row in rows.Values)
            builder.AppendLine(string.Join(',', row.Select(ValueText).Select(Csv)));
        return new UTF8Encoding(true).GetBytes(builder.ToString());
    }

    private static (string[] Headers, IReadOnlyList<object?[]> Values) BuildRows(string view, ProductionReportPage report) =>
        view switch
        {
            "materials" => (["Orden", "Proceso", "SKU", "Descripción", "Unidad", "Plan original", "Plan autorizado",
                    "Surtido", "Consumido", "Devuelto", "Desperdicio", "Pendiente de surtir", "Desviación plan",
                    "Consumo teórico", "Consumo primera pasada", "Desviación técnica"],
                report.Materials.Select(x => new object?[] { x.OrderNumber, x.Stage, x.Sku, x.Description, x.Unit,
                    x.OriginalPlan, x.AuthorizedPlan, x.Issued, x.Consumed, x.Returned, x.Scrapped, x.PendingSupply,
                    x.PlanVariance, x.TheoreticalConsumption, x.FirstPassConsumption, x.TechnicalVariance }).ToArray()),
            "rework" => (["Orden", "Proceso", "Unidad", "Inicial", "Recuperado", "Descartado", "Pendiente", "Intentos",
                    "Origen", "Antigüedad horas", "Umbral horas", "Alerta"],
                report.Rework.Select(x => new object?[] { x.OrderNumber, x.Stage, x.Unit, x.Initial, x.Recovered,
                    x.Discarded, x.Pending, x.Attempts, x.OriginAt, x.AgeHours, x.AlertHours, x.IsLate ? "Sí" : "No" }).ToArray()),
            "records" => (["Orden", "Tipo", "Proceso", "Turno", "Responsable", "Cantidad", "Bueno", "Retrabajo",
                    "Merma", "Motivo", "Fecha", "Efectivo"],
                report.Records.Select(x => new object?[] { x.OrderNumber, x.Type, x.Stage, x.Shift ?? "Sin turno registrado",
                    x.Responsible, x.Quantity, x.Good, x.Rework, x.Scrap, x.Reason, x.RecordedAt,
                    x.IsEffective ? "Sí" : "No" }).ToArray()),
            _ => (["Orden", "SKU", "Descripción", "Unidad", "Meta original", "Meta vigente", "Recibido", "Estado",
                    "Fecha requerida", "Vencida", "Alertas", "Última actividad"],
                report.Orders.Select(x => new object?[] { x.Number, x.Sku, x.Description, x.Unit, x.OriginalTarget,
                    x.Target, x.Received, x.Status.ToString(), x.DueDate, x.IsOverdue ? "Sí" : "No", x.AlertCount,
                    x.LastActivityAt }).ToArray())
        };

    private static void WriteExcel(IXLWorksheet sheet, string[] headers, IReadOnlyList<object?[]> rows)
    {
        const int headerRow = 5;
        for (var column = 0; column < headers.Length; column++) sheet.Cell(headerRow, column + 1).Value = headers[column];
        sheet.Range(headerRow, 1, headerRow, headers.Length).Style.Font.Bold = true;
        sheet.Range(headerRow, 1, headerRow, headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(headerRow);
        for (var index = 0; index < rows.Count; index++)
        {
            for (var column = 0; column < rows[index].Length; column++)
            {
                var cell = sheet.Cell(headerRow + index + 1, column + 1);
                switch (rows[index][column])
                {
                    case decimal number: cell.Value = number; cell.Style.NumberFormat.Format = "#,##0.0000"; break;
                    case int number: cell.Value = number; break;
                    case DateOnly date: cell.Value = date.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "yyyy-mm-dd"; break;
                    case DateTimeOffset instant: cell.Value = instant.DateTime; cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss"; break;
                    case null: cell.Value = ""; break;
                    default: cell.Value = Safe(Convert.ToString(rows[index][column], CultureInfo.InvariantCulture) ?? ""); break;
                }
            }
        }
        sheet.Columns().AdjustToContents(1, Math.Min(headerRow + rows.Count, headerRow + 200));
    }

    private static void EnsureLimit(int count)
    {
        if (count > RowLimit) throw new InvalidOperationException("La exportación supera 10,000 filas. Aplica filtros más específicos.");
    }

    private static string ValueText(object? value) => value switch
    {
        null => "",
        decimal number => number.ToString("0.####", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset instant => instant.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        _ => Safe(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
    };

    private static string Safe(string value) => value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? $"'{value}" : value;
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
