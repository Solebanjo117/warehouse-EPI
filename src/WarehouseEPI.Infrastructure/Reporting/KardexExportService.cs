using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed class KardexExportService(WarehouseSettingsService settingsService)
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@', '\t', '\r'];

    public async Task<byte[]> ToExcelAsync(
        KardexResult kardex,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Kardex");

        var scopeText = kardex.ScopedLocation is not null
            ? $"Ubicación: {kardex.ScopedLocation.Code}"
            : "Ámbito: Consolidado global (Almacén completo)";

        var periodText = kardex.FromUtc.HasValue && kardex.ToUtc.HasValue
            ? $"{TimeZoneInfo.ConvertTime(kardex.FromUtc.Value, timeZone):dd/MM/yyyy} a {TimeZoneInfo.ConvertTime(kardex.ToUtc.Value, timeZone):dd/MM/yyyy}"
            : kardex.FromUtc.HasValue
                ? $"Desde {TimeZoneInfo.ConvertTime(kardex.FromUtc.Value, timeZone):dd/MM/yyyy}"
                : "Todo el historial";

        // Title and metadata
        sheet.Cell(1, 1).SetValue(Safe($"{settings.WarehouseName} - Kardex de Producto"));
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;

        sheet.Cell(2, 1).SetValue(Safe($"SKU: {kardex.Product.Sku} | {kardex.Product.Description} | Unidad: {kardex.Product.BaseUnit.Code} | {scopeText} | Período: {periodText}"));
        sheet.Cell(2, 1).Style.Font.FontSize = 10;
        sheet.Cell(2, 1).Style.Font.Italic = true;

        sheet.Cell(3, 1).SetValue(Safe($"Saldo inicial: {kardex.Summary.InitialBalance:N4} | Entradas: {kardex.Summary.TotalEntries:N4} | Salidas: {kardex.Summary.TotalExits:N4} | Saldo al cierre: {kardex.Summary.EndingBalance:N4} | Existencia actual: {kardex.Summary.CurrentPhysicalBalance:N4} | Generado: {localNow:dd/MM/yyyy HH:mm:ss} ({settings.TimeZoneId})"));
        sheet.Cell(3, 1).Style.Font.FontSize = 9;

        // Header
        string[] headers =
        [
            "Fecha / Hora", "Tipo", "Propósito", "Referencia", "Ruta / Ubicación",
            "Lote interno", "Fecha lote", "Estado", "Responsable",
            "Entradas (+)", "Salidas (-)", "Saldo", "Alerta saldo", "Relación de corrección",
            "Motivo de corrección", "Fecha corrección", "Solicitó", "Autorizó", "Movimientos relacionados"
        ];

        var headerRow = 5;
        for (var col = 0; col < headers.Length; col++)
        {
            var cell = sheet.Cell(headerRow, col + 1);
            cell.SetValue(headers[col]);
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(15, 23, 42); // slate-900
            cell.Style.Alignment.Horizontal = col >= 9 ? XLAlignmentHorizontalValues.Right : XLAlignmentHorizontalValues.Left;
        }

        var rowIndex = headerRow + 1;

        // Initial balance row
        sheet.Cell(rowIndex, 1).SetValue(kardex.FromUtc.HasValue ? TimeZoneInfo.ConvertTime(kardex.FromUtc.Value, timeZone).DateTime : localNow.DateTime);
        sheet.Cell(rowIndex, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
        sheet.Cell(rowIndex, 2).SetValue("Saldo inicial");
        sheet.Cell(rowIndex, 2).Style.Font.Bold = true;
        sheet.Cell(rowIndex, 3).SetValue("Balance de apertura");
        sheet.Cell(rowIndex, 5).SetValue(scopeText);
        sheet.Cell(rowIndex, 8).SetValue("Apertura");
        sheet.Cell(rowIndex, 12).SetValue(kardex.Summary.InitialBalance);
        sheet.Cell(rowIndex, 12).Style.NumberFormat.Format = "#,##0.0000";
        sheet.Cell(rowIndex, 12).Style.Font.Bold = true;
        sheet.Row(rowIndex).Style.Fill.BackgroundColor = XLColor.FromArgb(241, 245, 249); // slate-100
        rowIndex++;

        var dataStartRow = rowIndex;

        foreach (var row in kardex.Rows)
        {
            var localTime = TimeZoneInfo.ConvertTime(row.OccurredAt, timeZone);
            sheet.Cell(rowIndex, 1).SetValue(localTime.DateTime);
            sheet.Cell(rowIndex, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";

            sheet.Cell(rowIndex, 2).SetValue(TypeLabel(row.Type));
            sheet.Cell(rowIndex, 3).SetValue(Safe(row.PurposeLabel));
            sheet.Cell(rowIndex, 4).SetValue(Safe(row.Reference));
            sheet.Cell(rowIndex, 5).SetValue(Safe(row.RouteOrLocation));
            sheet.Cell(rowIndex, 6).SetValue(Safe(row.LotNumber));

            if (row.LotDate.HasValue)
            {
                sheet.Cell(rowIndex, 7).SetValue(row.LotDate.Value.ToDateTime(TimeOnly.MinValue));
                sheet.Cell(rowIndex, 7).Style.DateFormat.Format = "yyyy-mm-dd";
            }

            sheet.Cell(rowIndex, 8).SetValue(Safe(row.Status));
            sheet.Cell(rowIndex, 9).SetValue(Safe(row.ResponsibleName));

            if (row.EntryQuantity > 0)
            {
                sheet.Cell(rowIndex, 10).SetValue(row.EntryQuantity);
                sheet.Cell(rowIndex, 10).Style.NumberFormat.Format = "#,##0.0000";
            }

            if (row.ExitQuantity > 0)
            {
                sheet.Cell(rowIndex, 11).SetValue(row.ExitQuantity);
                sheet.Cell(rowIndex, 11).Style.NumberFormat.Format = "#,##0.0000";
            }

            sheet.Cell(rowIndex, 12).SetValue(row.RunningBalance);
            sheet.Cell(rowIndex, 12).Style.NumberFormat.Format = "#,##0.0000";
            sheet.Cell(rowIndex, 13).SetValue(row.PassedToNegative ? "Pasa a negativo" : row.IsNegative ? "Saldo negativo" : string.Empty);
            sheet.Cell(rowIndex, 14).SetValue(Safe(string.Join(" | ", row.Corrections.Select(c => c.Relation))));
            sheet.Cell(rowIndex, 15).SetValue(Safe(string.Join(" | ", row.Corrections.Select(c => c.Reason))));
            sheet.Cell(rowIndex, 16).SetValue(Safe(string.Join(" | ", row.Corrections.Select(c => c.RecordedAtLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))));
            sheet.Cell(rowIndex, 17).SetValue(Safe(string.Join(" | ", row.Corrections.Select(c => c.RequestedBy))));
            sheet.Cell(rowIndex, 18).SetValue(Safe(string.Join(" | ", row.Corrections.Select(c => c.AuthorizedBy))));
            sheet.Cell(rowIndex, 19).SetValue(Safe(string.Join(" | ", row.Corrections.Select(c =>
                c.ReplacementMovementId.HasValue
                    ? $"Original {c.OriginalMovementId}; Reverso {c.ReversalMovementId}; Reemplazo {c.ReplacementMovementId}"
                    : $"Original {c.OriginalMovementId}; Reverso {c.ReversalMovementId}; Sin reemplazo"))));

            if (row.Status == "Reverso")
                sheet.Row(rowIndex).Style.Font.FontColor = XLColor.FromArgb(185, 28, 28); // red-700
            else if (row.Status == "Original corregido")
                sheet.Row(rowIndex).Style.Font.FontColor = XLColor.FromArgb(100, 116, 139); // slate-500

            rowIndex++;
        }

        var hasDataRows = rowIndex > dataStartRow;
        var lastDataRow = rowIndex - 1;

        // Summary row at bottom
        var summaryRow = rowIndex;
        sheet.Cell(summaryRow, 2).SetValue("TOTALES DEL PERÍODO");
        sheet.Cell(summaryRow, 2).Style.Font.Bold = true;

        if (hasDataRows)
        {
            sheet.Cell(summaryRow, 10).SetFormulaA1($"SUM(J{dataStartRow}:J{lastDataRow})");
            sheet.Cell(summaryRow, 11).SetFormulaA1($"SUM(K{dataStartRow}:K{lastDataRow})");
        }
        else
        {
            sheet.Cell(summaryRow, 10).SetValue(0m);
            sheet.Cell(summaryRow, 11).SetValue(0m);
        }

        sheet.Cell(summaryRow, 10).Style.NumberFormat.Format = "#,##0.0000";
        sheet.Cell(summaryRow, 10).Style.Font.Bold = true;
        sheet.Cell(summaryRow, 11).Style.NumberFormat.Format = "#,##0.0000";
        sheet.Cell(summaryRow, 11).Style.Font.Bold = true;

        sheet.Cell(summaryRow, 12).SetValue(kardex.Summary.EndingBalance);
        sheet.Cell(summaryRow, 12).Style.NumberFormat.Format = "#,##0.0000";
        sheet.Cell(summaryRow, 12).Style.Font.Bold = true;

        sheet.Row(summaryRow).Style.Fill.BackgroundColor = XLColor.FromArgb(226, 232, 240); // slate-200
        sheet.Row(summaryRow).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        sheet.Row(summaryRow).Style.Border.BottomBorder = XLBorderStyleValues.Double;

        sheet.Range(headerRow, 1, hasDataRows ? lastDataRow : headerRow, headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns().AdjustToContents(headerRow, summaryRow);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ToCsvAsync(
        KardexResult kardex,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);

        var scopeText = kardex.ScopedLocation is not null
            ? kardex.ScopedLocation.Code
            : "Consolidado global";

        var builder = new StringBuilder();

        // Metadata rows
        builder.AppendLine(Csv($"# Kardex de Producto: {Safe(kardex.Product.Sku)} - {Safe(kardex.Product.Description)}"));
        builder.AppendLine(Csv($"# Ámbito: {Safe(scopeText)} | Unidad: {Safe(kardex.Product.BaseUnit.Code)} | Zona horaria: {Safe(settings.TimeZoneId)}"));
        builder.AppendLine(Csv($"# Saldo inicial: {kardex.Summary.InitialBalance.ToString("0.0000", CultureInfo.InvariantCulture)} | Saldo al cierre: {kardex.Summary.EndingBalance.ToString("0.0000", CultureInfo.InvariantCulture)} | Existencia actual: {kardex.Summary.CurrentPhysicalBalance.ToString("0.0000", CultureInfo.InvariantCulture)}"));

        // Headers
        builder.AppendLine("Fecha / Hora,Tipo,Propósito,Referencia,Ruta / Ubicación,Lote interno,Fecha lote,Estado,Responsable,Entrada,Salida,Saldo progresivo,Alerta saldo,Relación de corrección,Motivo de corrección,Fecha corrección,Solicitó,Autorizó,Movimientos relacionados");

        // Initial balance row
        var initialDate = kardex.FromUtc.HasValue
            ? TimeZoneInfo.ConvertTime(kardex.FromUtc.Value, timeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : string.Empty;
        builder.AppendLine($"{Csv(initialDate)},\"Saldo inicial\",\"Apertura\",\"—\",{Csv(scopeText)},\"—\",\"—\",\"Apertura\",\"—\",0.0000,0.0000,{kardex.Summary.InitialBalance.ToString("0.0000", CultureInfo.InvariantCulture)},\"\",\"\",\"\",\"\",\"\",\"\",\"\"");

        // Data rows
        foreach (var row in kardex.Rows)
        {
            var localTime = TimeZoneInfo.ConvertTime(row.OccurredAt, timeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var lotDate = row.LotDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            var correctionRelations = string.Join(" | ", row.Corrections.Select(c => c.Relation));
            var correctionReasons = string.Join(" | ", row.Corrections.Select(c => c.Reason));
            var correctionDates = string.Join(" | ", row.Corrections.Select(c =>
                c.RecordedAtLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            var correctionRequesters = string.Join(" | ", row.Corrections.Select(c => c.RequestedBy));
            var correctionAuthorizers = string.Join(" | ", row.Corrections.Select(c => c.AuthorizedBy));
            var relatedMovements = string.Join(" | ", row.Corrections.Select(c => c.ReplacementMovementId.HasValue
                ? $"Original {c.OriginalMovementId}; Reverso {c.ReversalMovementId}; Reemplazo {c.ReplacementMovementId}"
                : $"Original {c.OriginalMovementId}; Reverso {c.ReversalMovementId}; Sin reemplazo"));

            builder.Append(Csv(localTime)).Append(',')
                .Append(Csv(TypeLabel(row.Type))).Append(',')
                .Append(Csv(row.PurposeLabel)).Append(',')
                .Append(Csv(Safe(row.Reference))).Append(',')
                .Append(Csv(Safe(row.RouteOrLocation))).Append(',')
                .Append(Csv(Safe(row.LotNumber))).Append(',')
                .Append(Csv(lotDate)).Append(',')
                .Append(Csv(row.Status)).Append(',')
                .Append(Csv(Safe(row.ResponsibleName))).Append(',')
                .Append(row.EntryQuantity.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.ExitQuantity.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.RunningBalance.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(row.PassedToNegative ? "Pasa a negativo" : row.IsNegative ? "Saldo negativo" : string.Empty)).Append(',')
                .Append(Csv(Safe(correctionRelations))).Append(',')
                .Append(Csv(Safe(correctionReasons))).Append(',')
                .Append(Csv(Safe(correctionDates))).Append(',')
                .Append(Csv(Safe(correctionRequesters))).Append(',')
                .Append(Csv(Safe(correctionAuthorizers))).Append(',')
                .Append(Csv(Safe(relatedMovements)))
                .AppendLine();
        }

        var encoding = new UTF8Encoding(true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(builder.ToString())];
    }

    private static string TypeLabel(InventoryMovementType type) => type switch
    {
        InventoryMovementType.Entry => "Entrada",
        InventoryMovementType.Exit => "Salida",
        InventoryMovementType.Transfer => "Transferencia",
        InventoryMovementType.Adjustment => "Ajuste",
        _ => type.ToString()
    };

    private static string Safe(string? value)
    {
        var text = value ?? string.Empty;
        return text.Length > 0 && FormulaPrefixes.Contains(text[0]) ? "'" + text : text;
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
