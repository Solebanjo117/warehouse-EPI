using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

/// <summary>
/// Servicio de exportación segura de reportes a Microsoft Excel (.xlsx) y CSV.
/// Aplica defensas contra inyección de fórmulas (Formula Injection) en campos de texto,
/// utiliza tipos numéricos y fechas nativas, y garantiza codificación UTF-8 con BOM en CSV.
/// </summary>
public sealed class ReportExportService(WarehouseSettingsService settingsService)
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// Exporta el listado de movimientos efectivos a un archivo Excel (.xlsx) formateado con ClosedXML.
    /// </summary>
    public async Task<byte[]> ExportMovementsToExcelAsync(
        IReadOnlyList<EffectiveMovementRowDto> movements,
        MovementReportFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Movimientos");

        // Encabezado institucional
        worksheet.Cell(1, 1).SetValue(SanitizeText($"{settings.WarehouseName} - Reporte de Movimientos Efectivos"));
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;

        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var totalLines = movements.Sum(m => m.Lines.Count);
        worksheet.Cell(2, 1).SetValue(SanitizeText($"Generado el: {localNow:yyyy-MM-dd HH:mm:ss} ({settings.TimeZoneId}) | Total: {totalLines} líneas en {movements.Count} operaciones"));
        worksheet.Cell(2, 1).Style.Font.Italic = true;
        worksheet.Cell(3, 1).SetValue(SanitizeText($"Filtros: {FormatFilter(filter)}"));
        worksheet.Cell(3, 1).Style.Font.Italic = true;

        // Fila de encabezados de columna
        var headerRow = 5;
        string[] headers =
        [
            "Folio",
            "Fecha / Hora",
            "Tipo",
            "Propósito",
            "Responsable",
            "Referencia",
            "Notas",
            "SKU",
            "Descripción",
            "Cantidad capturada",
            "Unidad",
            "Origen",
            "Destino",
            "Saldo anterior",
            "Diferencia ajuste",
            "Saldo resultante",
            "Modo de asignación",
            "Cambios por ubicación y lote"
        ];

        for (var col = 0; col < headers.Length; col++)
        {
            var cell = worksheet.Cell(headerRow, col + 1);
            cell.SetValue(headers[col]);
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(15, 23, 42); // Azul petróleo oscuro / Slate
            cell.Style.Alignment.Horizontal = col == 9 ? XLAlignmentHorizontalValues.Right : XLAlignmentHorizontalValues.Left;
        }

        var currentRow = headerRow + 1;
        foreach (var movement in movements)
        {
            var localOccurredAt = TimeZoneInfo.ConvertTime(movement.OccurredAt, timeZone);

            foreach (var line in movement.Lines)
            {
                worksheet.Cell(currentRow, 1).SetValue(SanitizeText(movement.Id.ToString()));

                // Fecha nativa en Excel
                var dateCell = worksheet.Cell(currentRow, 2);
                dateCell.SetValue(localOccurredAt.DateTime);
                dateCell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";

                worksheet.Cell(currentRow, 3).SetValue(SanitizeText(FormatMovementType(movement.MovementType)));
                worksheet.Cell(currentRow, 4).SetValue(SanitizeText(FormatPurpose(movement.Purpose)));
                worksheet.Cell(currentRow, 5).SetValue(SanitizeText(movement.ResponsibleName));
                worksheet.Cell(currentRow, 6).SetValue(SanitizeText(movement.Reference ?? string.Empty));
                worksheet.Cell(currentRow, 7).SetValue(SanitizeText(movement.Notes ?? string.Empty));
                worksheet.Cell(currentRow, 8).SetValue(SanitizeText(line.Sku));
                worksheet.Cell(currentRow, 9).SetValue(SanitizeText(line.ProductDescription ?? string.Empty));

                // Cantidad numérica real con 4 decimales
                var qtyCell = worksheet.Cell(currentRow, 10);
                qtyCell.SetValue(line.Quantity);
                qtyCell.Style.NumberFormat.Format = "#,##0.0000";

                worksheet.Cell(currentRow, 11).SetValue(SanitizeText(line.UnitCode));
                worksheet.Cell(currentRow, 12).SetValue(SanitizeText(line.SourceLocationCode ?? string.Empty));
                worksheet.Cell(currentRow, 13).SetValue(SanitizeText(line.DestinationLocationCode ?? movement.OperationalAreaCode ?? string.Empty));

                SetNullableNumber(worksheet.Cell(currentRow, 14), line.PreviousQuantity);
                SetNullableNumber(worksheet.Cell(currentRow, 15), line.AdjustmentDelta);
                SetNullableNumber(
                    worksheet.Cell(currentRow, 16),
                    movement.MovementType == InventoryMovementType.Adjustment ? line.Quantity : null);
                worksheet.Cell(currentRow, 17).SetValue(SanitizeText(line.AllocationMode));
                worksheet.Cell(currentRow, 18).SetValue(SanitizeText(FormatBalanceChanges(line.BalanceChanges)));

                currentRow++;
            }
        }

        worksheet.Columns().AdjustToContents(4, Math.Max(5, currentRow - 1));

        using var memoryStream = new MemoryStream();
        workbook.SaveAs(memoryStream);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// Exporta el listado de movimientos efectivos a formato CSV compatible con RFC 4180 y UTF-8 con BOM.
    /// </summary>
    public async Task<byte[]> ExportMovementsToCsvAsync(
        IReadOnlyList<EffectiveMovementRowDto> movements,
        MovementReportFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);

        var sb = new StringBuilder();

        // Cabecera CSV
        sb.AppendLine("Folio,Fecha / Hora,Tipo,Propósito,Responsable,Referencia,Notas,SKU,Descripción,Cantidad capturada,Unidad,Origen,Destino,Saldo anterior,Diferencia ajuste,Saldo resultante,Modo de asignación,Cambios por ubicación y lote,Zona horaria,Filtros aplicados");

        var filterDescription = FormatFilter(filter);

        foreach (var movement in movements)
        {
            var localOccurredAt = TimeZoneInfo.ConvertTime(movement.OccurredAt, timeZone);

            foreach (var line in movement.Lines)
            {
                sb.Append(EscapeCsv(SanitizeText(movement.Id.ToString()))).Append(',');
                sb.Append(EscapeCsv(localOccurredAt.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(FormatMovementType(movement.MovementType)))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(FormatPurpose(movement.Purpose)))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(movement.ResponsibleName))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(movement.Reference ?? string.Empty))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(movement.Notes ?? string.Empty))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(line.Sku))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(line.ProductDescription ?? string.Empty))).Append(',');

                // Cantidad numérica sin comillas de fórmula (preserva signo y precisión)
                sb.Append(line.Quantity.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');

                sb.Append(EscapeCsv(SanitizeText(line.UnitCode))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(line.SourceLocationCode ?? string.Empty))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(line.DestinationLocationCode ?? movement.OperationalAreaCode ?? string.Empty))).Append(',');
                sb.Append(FormatNullableNumber(line.PreviousQuantity)).Append(',');
                sb.Append(FormatNullableNumber(line.AdjustmentDelta)).Append(',');
                sb.Append(FormatNullableNumber(
                    movement.MovementType == InventoryMovementType.Adjustment ? line.Quantity : null)).Append(',');
                sb.Append(EscapeCsv(SanitizeText(line.AllocationMode))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(FormatBalanceChanges(line.BalanceChanges)))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).Append(',');
                sb.Append(EscapeCsv(SanitizeText(filterDescription)));
                sb.AppendLine();
            }
        }

        // Retornar UTF-8 con BOM
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(sb.ToString());
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static void SetNullableNumber(IXLCell cell, decimal? value)
    {
        if (value is null)
            return;

        cell.SetValue(value.Value);
        cell.Style.NumberFormat.Format = "#,##0.0000";
    }

    private static string FormatNullableNumber(decimal? value) =>
        value?.ToString("0.0000", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatBalanceChanges(IReadOnlyList<EffectiveMovementBalanceChangeDto> changes) =>
        string.Join(
            "; ",
            changes.Select(change =>
                $"{change.LocationCode} | {change.LotNumber ?? "Sin lote"} | Δ {change.DeltaQuantity.ToString("0.####", CultureInfo.InvariantCulture)} | {change.PreviousQuantity.ToString("0.####", CultureInfo.InvariantCulture)}→{change.ResultingQuantity.ToString("0.####", CultureInfo.InvariantCulture)}"));

    private static string FormatFilter(MovementReportFilter? filter)
    {
        if (filter is null)
            return "Sin filtros";

        var values = new List<string>();
        if (filter.FromUtc is not null) values.Add($"desde UTC {filter.FromUtc:yyyy-MM-dd HH:mm:ss}");
        if (filter.ToUtc is not null) values.Add($"hasta UTC exclusivo {filter.ToUtc:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(filter.Search)) values.Add($"búsqueda={filter.Search}");
        if (!string.IsNullOrWhiteSpace(filter.Sku)) values.Add($"producto={filter.Sku}");
        if (!string.IsNullOrWhiteSpace(filter.LocationCode)) values.Add($"ubicación={filter.LocationCode}");
        if (filter.MovementType is not null) values.Add($"tipo={filter.MovementType}");
        if (filter.Purpose is not null) values.Add($"propósito={filter.Purpose}");
        if (filter.ResponsibleUserId is not null) values.Add($"responsable={filter.ResponsibleUserId}");
        return values.Count == 0 ? "Sin filtros" : string.Join(" | ", values);
    }

    /// <summary>
    /// Sanitiza un valor textual contra inyección de fórmulas en hojas de cálculo.
    /// Solo se aplica a cadenas de texto; los valores numéricos nunca deben pasar por este método.
    /// </summary>
    public static string SanitizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value.StartsWith('\t') || value.StartsWith('\r'))
            return $"'{value}";

        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && FormulaPrefixes.Contains(trimmed[0]))
        {
            return $"'{value}";
        }

        return value;
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return $"\"{value}\"";
    }

    private static string FormatMovementType(InventoryMovementType type) => type switch
    {
        InventoryMovementType.Entry => "ENTRADA",
        InventoryMovementType.Exit => "SALIDA",
        InventoryMovementType.Transfer => "TRANSFERENCIA",
        InventoryMovementType.Adjustment => "AJUSTE",
        _ => type.ToString()
    };

    private static string FormatPurpose(InventoryMovementPurpose purpose) => purpose switch
    {
        InventoryMovementPurpose.Standard => "Estándar",
        InventoryMovementPurpose.GeneralExit => "Salida general",
        InventoryMovementPurpose.ProductionIssue => "Surtimiento WIP",
        InventoryMovementPurpose.WipWarehouseReturn => "Devolución WIP",
        _ => purpose.ToString()
    };
}
