using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
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

    public async Task<byte[]> ExportMovementAuditToExcelAsync(
        IReadOnlyList<InventoryMovementTraceRow> rows,
        InventoryHistoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Auditoría");
        WriteInventoryHeader(
            worksheet,
            $"{settings.WarehouseName} - Auditoría completa de movimientos",
            rows.Count,
            settings.TimeZoneId,
            FormatHistoryFilter(filter),
            timeZone,
            "filas");
        string[] headers =
        [
            "Movimiento", "Operación", "Tipo", "Propósito", "Estado", "Fecha / Hora",
            "Responsable", "Referencia", "Notas", "SKU", "Producto", "Unidad",
            "Cantidad capturada", "Origen", "Destino", "Área operativa", "Ubicación histórica",
            "Lote histórico", "Fecha lote", "Asignación", "Saldo anterior", "Diferencia", "Saldo resultante"
        ];
        WriteTableHeaders(worksheet, headers);
        var currentRow = 6;
        foreach (var row in rows)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(row.MovementId.ToString()));
            worksheet.Cell(currentRow, 2).SetValue(SanitizeText(row.OperationId.ToString()));
            worksheet.Cell(currentRow, 3).SetValue(SanitizeText(FormatMovementType(row.Type)));
            worksheet.Cell(currentRow, 4).SetValue(SanitizeText(FormatPurpose(row.Purpose)));
            worksheet.Cell(currentRow, 5).SetValue(SanitizeText(row.Status));
            SetLocalDate(worksheet.Cell(currentRow, 6), row.OccurredAt, timeZone);
            worksheet.Cell(currentRow, 7).SetValue(SanitizeText(row.Responsible));
            worksheet.Cell(currentRow, 8).SetValue(SanitizeText(row.Reference));
            worksheet.Cell(currentRow, 9).SetValue(SanitizeText(row.Notes));
            worksheet.Cell(currentRow, 10).SetValue(SanitizeText(row.ProductSku));
            worksheet.Cell(currentRow, 11).SetValue(SanitizeText(row.ProductDescription));
            worksheet.Cell(currentRow, 12).SetValue(SanitizeText(row.Unit));
            SetNumber(worksheet.Cell(currentRow, 13), row.CapturedQuantity);
            worksheet.Cell(currentRow, 14).SetValue(SanitizeText(row.Source));
            worksheet.Cell(currentRow, 15).SetValue(SanitizeText(row.Destination));
            worksheet.Cell(currentRow, 16).SetValue(SanitizeText(row.OperationalArea));
            worksheet.Cell(currentRow, 17).SetValue(SanitizeText(row.Location));
            worksheet.Cell(currentRow, 18).SetValue(SanitizeText(row.LotNumber));
            if (row.LotDate is not null)
            {
                worksheet.Cell(currentRow, 19).SetValue(row.LotDate.Value.ToDateTime(TimeOnly.MinValue));
                worksheet.Cell(currentRow, 19).Style.DateFormat.Format = "yyyy-mm-dd";
            }
            worksheet.Cell(currentRow, 20).SetValue(SanitizeText(row.AllocationMode));
            SetNullableNumber(worksheet.Cell(currentRow, 21), row.Previous);
            SetNullableNumber(worksheet.Cell(currentRow, 22), row.Delta);
            SetNullableNumber(worksheet.Cell(currentRow, 23), row.Resulting);
            currentRow++;
        }
        worksheet.Columns().AdjustToContents(4, Math.Max(5, currentRow - 1));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportMovementAuditToCsvAsync(
        IReadOnlyList<InventoryMovementTraceRow> rows,
        InventoryHistoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var metadata = FormatHistoryFilter(filter);
        var builder = new StringBuilder();
        builder.AppendLine("Movimiento,Operación,Tipo,Propósito,Estado,Fecha / Hora,Responsable,Referencia,Notas,SKU,Producto,Unidad,Cantidad capturada,Origen,Destino,Área operativa,Ubicación histórica,Lote histórico,Fecha lote,Asignación,Saldo anterior,Diferencia,Saldo resultante,Zona horaria,Filtros aplicados");
        foreach (var row in rows)
        {
            builder.Append(EscapeCsv(SanitizeText(row.MovementId.ToString()))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.OperationId.ToString()))).Append(',')
                .Append(EscapeCsv(SanitizeText(FormatMovementType(row.Type)))).Append(',')
                .Append(EscapeCsv(SanitizeText(FormatPurpose(row.Purpose)))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.Status))).Append(',')
                .Append(EscapeCsv(TimeZoneInfo.ConvertTime(row.OccurredAt, timeZone).ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.Responsible))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.Reference))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.Notes))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.ProductSku))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.ProductDescription))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.Unit))).Append(',')
                .Append(row.CapturedQuantity.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
                .Append(EscapeCsv(SanitizeText(row.Source))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.Destination))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.OperationalArea))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.Location))).Append(',')
                .Append(EscapeCsv(SanitizeText(row.LotNumber))).Append(',')
                .Append(EscapeCsv(row.LotDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty)).Append(',')
                .Append(EscapeCsv(SanitizeText(row.AllocationMode))).Append(',')
                .Append(FormatNullableNumber(row.Previous)).Append(',')
                .Append(FormatNullableNumber(row.Delta)).Append(',')
                .Append(FormatNullableNumber(row.Resulting)).Append(',')
                .Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).Append(',')
                .Append(EscapeCsv(SanitizeText(metadata))).AppendLine();
        }
        return CsvBytes(builder);
    }

    public async Task<byte[]> ExportExitActivityToExcelAsync(
        IReadOnlyList<SkuExitActivityMetricDto> rows,
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Actividad salidas");
        WriteInventoryHeader(
            worksheet,
            $"{settings.WarehouseName} - Actividad de salidas por SKU",
            rows.Count,
            settings.TimeZoneId,
            FormatAnalyticsFilter(filter),
            timeZone);

        string[] headers =
        [
            "SKU", "Descripción", "Estado", "Unidad", "Salidas efectivas",
            "Cantidad movilizada", "Existencia actual", "Última salida"
        ];
        WriteTableHeaders(worksheet, headers);
        var currentRow = 6;
        foreach (var row in rows)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(row.Sku));
            worksheet.Cell(currentRow, 2).SetValue(SanitizeText(row.Description ?? string.Empty));
            worksheet.Cell(currentRow, 3).SetValue(FormatProductState(row.IsActive));
            worksheet.Cell(currentRow, 4).SetValue(SanitizeText(row.UnitCode));
            worksheet.Cell(currentRow, 5).SetValue(row.EffectiveExitMovementCount);
            SetNumber(worksheet.Cell(currentRow, 6), row.QuantityInBaseUnit);
            SetNumber(worksheet.Cell(currentRow, 7), row.CurrentStock);
            SetLocalDate(worksheet.Cell(currentRow, 8), row.LastExitDateUtc, timeZone);
            currentRow++;
        }
        worksheet.Columns().AdjustToContents(4, Math.Max(5, currentRow - 1));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportExitActivityToCsvAsync(
        IReadOnlyList<SkuExitActivityMetricDto> rows,
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var metadata = FormatAnalyticsFilter(filter);
        var sb = new StringBuilder();
        sb.AppendLine("SKU,Descripción,Estado,Unidad,Salidas efectivas,Cantidad movilizada,Existencia actual,Última salida,Zona horaria,Filtros aplicados");
        foreach (var row in rows)
        {
            sb.Append(EscapeCsv(SanitizeText(row.Sku))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.Description ?? string.Empty))).Append(',');
            sb.Append(EscapeCsv(FormatProductState(row.IsActive))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.UnitCode))).Append(',');
            sb.Append(row.EffectiveExitMovementCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(row.QuantityInBaseUnit.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(row.CurrentStock.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(EscapeCsv(FormatLocalDate(row.LastExitDateUtc, timeZone))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(metadata))).AppendLine();
        }
        return CsvBytes(sb);
    }

    public async Task<byte[]> ExportStagnantToExcelAsync(
        IReadOnlyList<StagnantProductDto> rows,
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Estancamiento");
        WriteInventoryHeader(
            worksheet,
            $"{settings.WarehouseName} - Productos estancados",
            rows.Count,
            settings.TimeZoneId,
            FormatAnalyticsFilter(filter),
            timeZone);

        string[] headers =
        [
            "SKU", "Descripción", "Estado", "Unidad", "Existencia actual",
            "Última salida", "Días sin salida", "Categoría"
        ];
        WriteTableHeaders(worksheet, headers);
        var currentRow = 6;
        foreach (var row in rows)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(row.Sku));
            worksheet.Cell(currentRow, 2).SetValue(SanitizeText(row.Description ?? string.Empty));
            worksheet.Cell(currentRow, 3).SetValue(FormatProductState(row.IsActive));
            worksheet.Cell(currentRow, 4).SetValue(SanitizeText(row.UnitCode));
            SetNumber(worksheet.Cell(currentRow, 5), row.CurrentStock);
            SetLocalDate(worksheet.Cell(currentRow, 6), row.LastExitDateUtc, timeZone);
            if (row.DaysWithoutExit is not null)
                worksheet.Cell(currentRow, 7).SetValue(row.DaysWithoutExit.Value);
            worksheet.Cell(currentRow, 8).SetValue(FormatStagnantCategory(row.Category));
            currentRow++;
        }
        worksheet.Columns().AdjustToContents(4, Math.Max(5, currentRow - 1));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportStagnantToCsvAsync(
        IReadOnlyList<StagnantProductDto> rows,
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var metadata = FormatAnalyticsFilter(filter);
        var sb = new StringBuilder();
        sb.AppendLine("SKU,Descripción,Estado,Unidad,Existencia actual,Última salida,Días sin salida,Categoría,Zona horaria,Filtros aplicados");
        foreach (var row in rows)
        {
            sb.Append(EscapeCsv(SanitizeText(row.Sku))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.Description ?? string.Empty))).Append(',');
            sb.Append(EscapeCsv(FormatProductState(row.IsActive))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.UnitCode))).Append(',');
            sb.Append(row.CurrentStock.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(EscapeCsv(FormatLocalDate(row.LastExitDateUtc, timeZone))).Append(',');
            sb.Append(row.DaysWithoutExit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
            sb.Append(EscapeCsv(FormatStagnantCategory(row.Category))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(metadata))).AppendLine();
        }
        return CsvBytes(sb);
    }

    public async Task<byte[]> ExportNegativeExceptionsToExcelAsync(
        IReadOnlyList<NegativeInventoryAlert> rows,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Saldos negativos");
        WriteInventoryHeader(
            worksheet,
            $"{settings.WarehouseName} - Saldos producto-ubicación negativos",
            rows.Count,
            settings.TimeZoneId,
            FormatExceptionFilter(search),
            timeZone);

        WriteTableHeaders(worksheet,
        [
            "SKU", "Descripción", "Unidad", "Ubicación", "Descripción ubicación", "Saldo actual"
        ]);
        var currentRow = 6;
        foreach (var row in rows)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(row.ProductSku));
            worksheet.Cell(currentRow, 2).SetValue(SanitizeText(row.ProductDescription ?? string.Empty));
            worksheet.Cell(currentRow, 3).SetValue(SanitizeText(row.UnitCode));
            worksheet.Cell(currentRow, 4).SetValue(SanitizeText(row.LocationCode));
            worksheet.Cell(currentRow, 5).SetValue(SanitizeText(row.LocationDescription ?? string.Empty));
            SetNumber(worksheet.Cell(currentRow, 6), row.Quantity);
            currentRow++;
        }
        worksheet.Columns().AdjustToContents(4, Math.Max(5, currentRow - 1));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportNegativeExceptionsToCsvAsync(
        IReadOnlyList<NegativeInventoryAlert> rows,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var metadata = FormatExceptionFilter(search);
        var sb = new StringBuilder();
        sb.AppendLine("SKU,Descripción,Unidad,Ubicación,Descripción ubicación,Saldo actual,Zona horaria,Filtros aplicados");
        foreach (var row in rows)
        {
            sb.Append(EscapeCsv(SanitizeText(row.ProductSku))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.ProductDescription ?? string.Empty))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.UnitCode))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.LocationCode))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.LocationDescription ?? string.Empty))).Append(',');
            sb.Append(row.Quantity.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(metadata))).AppendLine();
        }
        return CsvBytes(sb);
    }

    public async Task<byte[]> ExportMinimumExceptionsToExcelAsync(
        IReadOnlyList<MinimumStockInventoryAlert> rows,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Bajo mínimo");
        WriteInventoryHeader(
            worksheet,
            $"{settings.WarehouseName} - Productos bajo mínimo",
            rows.Count,
            settings.TimeZoneId,
            FormatExceptionFilter(search),
            timeZone);

        WriteTableHeaders(worksheet,
        [
            "SKU", "Descripción", "Unidad", "Existencia actual", "Mínimo", "Faltante", "Cobertura"
        ]);
        var currentRow = 6;
        foreach (var row in rows)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(row.Sku));
            worksheet.Cell(currentRow, 2).SetValue(SanitizeText(row.Description ?? string.Empty));
            worksheet.Cell(currentRow, 3).SetValue(SanitizeText(row.UnitCode));
            SetNumber(worksheet.Cell(currentRow, 4), row.TotalQuantity);
            SetNumber(worksheet.Cell(currentRow, 5), row.MinimumStock);
            SetNumber(worksheet.Cell(currentRow, 6), row.Deficit);
            if (row.CoveragePercent is decimal coverage)
                SetNumber(worksheet.Cell(currentRow, 7), coverage);
            currentRow++;
        }
        worksheet.Columns().AdjustToContents(4, Math.Max(5, currentRow - 1));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportMinimumExceptionsToCsvAsync(
        IReadOnlyList<MinimumStockInventoryAlert> rows,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var metadata = FormatExceptionFilter(search);
        var sb = new StringBuilder();
        sb.AppendLine("SKU,Descripción,Unidad,Existencia actual,Mínimo,Faltante,Cobertura,Zona horaria,Filtros aplicados");
        foreach (var row in rows)
        {
            sb.Append(EscapeCsv(SanitizeText(row.Sku))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.Description ?? string.Empty))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(row.UnitCode))).Append(',');
            sb.Append(row.TotalQuantity.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(row.MinimumStock.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(row.Deficit.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(row.CoveragePercent?.ToString("0.0000", CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
            sb.Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).Append(',');
            sb.Append(EscapeCsv(SanitizeText(metadata))).AppendLine();
        }
        return CsvBytes(sb);
    }

    public async Task<byte[]> ExportCycleCountsToExcelAsync(IReadOnlyList<CycleCountExportRow> rows, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Conteos cíclicos");
        WriteInventoryHeader(worksheet, $"{settings.WarehouseName} - Conteos cíclicos", rows.Count, settings.TimeZoneId, "Resultados de campaña", timeZone);
        WriteTableHeaders(worksheet, ["Folio", "Ubicación", "Intento", "SKU", "Descripción", "Unidad", "Esperado", "Contado", "Diferencia", "Inesperado", "Estado", "Inicio", "Enviado"]);
        var currentRow = 6;
        foreach (var row in rows)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(row.Folio)); worksheet.Cell(currentRow, 2).SetValue(SanitizeText(row.LocationCode)); worksheet.Cell(currentRow, 3).SetValue(row.AttemptNumber);
            worksheet.Cell(currentRow, 4).SetValue(SanitizeText(row.Sku)); worksheet.Cell(currentRow, 5).SetValue(SanitizeText(row.Description)); worksheet.Cell(currentRow, 6).SetValue(SanitizeText(row.UnitCode));
            worksheet.Cell(currentRow, 7).SetValue(row.ExpectedQuantity); SetNullableNumber(worksheet.Cell(currentRow, 8), row.CountedQuantity); SetNullableNumber(worksheet.Cell(currentRow, 9), row.Difference);
            worksheet.Cell(currentRow, 10).SetValue(row.IsUnexpectedProduct ? "Sí" : "No"); worksheet.Cell(currentRow, 11).SetValue(SanitizeText(row.LocationStatus.ToString()));
            worksheet.Cell(currentRow, 12).SetValue(TimeZoneInfo.ConvertTime(row.StartedAt, timeZone).DateTime); worksheet.Cell(currentRow, 12).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            if (row.SubmittedAt is not null) { worksheet.Cell(currentRow, 13).SetValue(TimeZoneInfo.ConvertTime(row.SubmittedAt.Value, timeZone).DateTime); worksheet.Cell(currentRow, 13).Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; }
            foreach (var column in new[] { 7, 8, 9 }) worksheet.Cell(currentRow, column).Style.NumberFormat.Format = "#,##0.0000";
            currentRow++;
        }
        worksheet.Columns().AdjustToContents();
        using var stream = new MemoryStream(); workbook.SaveAs(stream); return stream.ToArray();
    }

    public async Task<byte[]> ExportCycleCountsToCsvAsync(IReadOnlyList<CycleCountExportRow> rows, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var builder = new StringBuilder("Folio,Ubicación,Intento,SKU,Descripción,Unidad,Esperado,Contado,Diferencia,Inesperado,Estado,Inicio,Enviado,Zona horaria\r\n");
        foreach (var row in rows)
        {
            builder.Append(EscapeCsv(SanitizeText(row.Folio))).Append(',').Append(EscapeCsv(SanitizeText(row.LocationCode))).Append(',').Append(row.AttemptNumber).Append(',')
                .Append(EscapeCsv(SanitizeText(row.Sku))).Append(',').Append(EscapeCsv(SanitizeText(row.Description))).Append(',').Append(EscapeCsv(SanitizeText(row.UnitCode))).Append(',')
                .Append(row.ExpectedQuantity.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',').Append(FormatNullableNumber(row.CountedQuantity)).Append(',').Append(FormatNullableNumber(row.Difference)).Append(',')
                .Append(EscapeCsv(row.IsUnexpectedProduct ? "Sí" : "No")).Append(',').Append(EscapeCsv(SanitizeText(row.LocationStatus.ToString()))).Append(',')
                .Append(EscapeCsv(TimeZoneInfo.ConvertTime(row.StartedAt, timeZone).ToString("yyyy-MM-dd HH:mm"))).Append(',').Append(EscapeCsv(row.SubmittedAt is null ? string.Empty : TimeZoneInfo.ConvertTime(row.SubmittedAt.Value, timeZone).ToString("yyyy-MM-dd HH:mm"))).Append(',')
                .Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).AppendLine();
        }
        var encoding = new UTF8Encoding(true); return encoding.GetPreamble().Concat(encoding.GetBytes(builder.ToString())).ToArray();
    }

    public async Task<byte[]> ExportOccupancyToExcelAsync(
        LocationOccupancyReportDto occupancy,
        DateTimeOffset snapshotAtUtc,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Ocupación");

        var localGeneratedAt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var localSnapshotAt = TimeZoneInfo.ConvertTime(snapshotAtUtc, timeZone);

        worksheet.Cell(1, 1).SetValue(SanitizeText($"{settings.WarehouseName} - Ocupación física de racks"));
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;

        worksheet.Cell(2, 1).SetValue(SanitizeText(
            $"Generado el: {localGeneratedAt:yyyy-MM-dd HH:mm:ss} ({settings.TimeZoneId}) | Datos al: {localSnapshotAt:yyyy-MM-dd HH:mm:ss} | Total: {occupancy.Rows.Count} filas"));
        worksheet.Cell(2, 1).Style.Font.Italic = true;

        worksheet.Cell(3, 1).SetValue(SanitizeText(
            $"Posiciones totales: {occupancy.Summary.TotalStoragePositions:N0} | Ocupadas: {occupancy.Summary.OccupiedCount:N0} | Vacías: {occupancy.Summary.EmptyCount:N0} | Negativas: {occupancy.Summary.NegativeCount:N0} | Bloqueadas: {occupancy.Summary.BlockedCount:N0} | Inactivas: {occupancy.Summary.InactiveCount:N0} | Utilización global: {(occupancy.Summary.UtilizationPercentage / 100m):0.00%}"));
        worksheet.Cell(3, 1).Style.Font.Italic = true;

        string[] headers =
        [
            "Fila", "Posiciones de almacenamiento", "Ocupadas", "Vacías",
            "Negativas", "Bloqueadas", "Inactivas", "% Utilización"
        ];
        WriteTableHeaders(worksheet, headers);

        var currentRow = 6;
        foreach (var row in occupancy.Rows)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(row.RowCode));
            SetCount(worksheet.Cell(currentRow, 2), row.Summary.TotalStoragePositions);
            SetCount(worksheet.Cell(currentRow, 3), row.Summary.OccupiedCount);
            SetCount(worksheet.Cell(currentRow, 4), row.Summary.EmptyCount);
            SetCount(worksheet.Cell(currentRow, 5), row.Summary.NegativeCount);
            SetCount(worksheet.Cell(currentRow, 6), row.Summary.BlockedCount);
            SetCount(worksheet.Cell(currentRow, 7), row.Summary.InactiveCount);
            SetPercentage(worksheet.Cell(currentRow, 8), row.Summary.UtilizationPercentage);
            currentRow++;
        }

        // Fila final de Totales / Resumen consolidado
        var totalCell = worksheet.Cell(currentRow, 1);
        totalCell.SetValue("Total general");
        totalCell.Style.Font.Bold = true;

        SetCount(worksheet.Cell(currentRow, 2), occupancy.Summary.TotalStoragePositions, isBold: true);
        SetCount(worksheet.Cell(currentRow, 3), occupancy.Summary.OccupiedCount, isBold: true);
        SetCount(worksheet.Cell(currentRow, 4), occupancy.Summary.EmptyCount, isBold: true);
        SetCount(worksheet.Cell(currentRow, 5), occupancy.Summary.NegativeCount, isBold: true);
        SetCount(worksheet.Cell(currentRow, 6), occupancy.Summary.BlockedCount, isBold: true);
        SetCount(worksheet.Cell(currentRow, 7), occupancy.Summary.InactiveCount, isBold: true);
        SetPercentage(worksheet.Cell(currentRow, 8), occupancy.Summary.UtilizationPercentage, isBold: true);

        worksheet.Range(currentRow, 1, currentRow, 8).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        worksheet.Range(currentRow, 1, currentRow, 8).Style.Fill.BackgroundColor = XLColor.FromArgb(241, 245, 249);

        worksheet.Columns().AdjustToContents(4, Math.Max(5, currentRow));

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportOccupancyToCsvAsync(
        LocationOccupancyReportDto occupancy,
        DateTimeOffset snapshotAtUtc,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var localGeneratedAt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var localSnapshotAt = TimeZoneInfo.ConvertTime(snapshotAtUtc, timeZone);

        var sb = new StringBuilder();
        sb.AppendLine("Fila,Posiciones de almacenamiento,Ocupadas,Vacías,Negativas,Bloqueadas,Inactivas,Utilización,Fecha generación,Fecha datos,Zona horaria");

        foreach (var row in occupancy.Rows)
        {
            sb.Append(EscapeCsv(SanitizeText(row.RowCode))).Append(',')
              .Append(row.Summary.TotalStoragePositions.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.Summary.OccupiedCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.Summary.EmptyCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.Summary.NegativeCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.Summary.BlockedCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(row.Summary.InactiveCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append((row.Summary.UtilizationPercentage / 100m).ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
              .Append(EscapeCsv(localGeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(EscapeCsv(localSnapshotAt.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).AppendLine();
        }

        // Fila de total
        sb.Append(EscapeCsv("Total general")).Append(',')
          .Append(occupancy.Summary.TotalStoragePositions.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(occupancy.Summary.OccupiedCount.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(occupancy.Summary.EmptyCount.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(occupancy.Summary.NegativeCount.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(occupancy.Summary.BlockedCount.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(occupancy.Summary.InactiveCount.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append((occupancy.Summary.UtilizationPercentage / 100m).ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
          .Append(EscapeCsv(localGeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
          .Append(EscapeCsv(localSnapshotAt.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
          .Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).AppendLine();

        return CsvBytes(sb);
    }

    public async Task<byte[]> ExportLotAgingToExcelAsync(
        IReadOnlyList<LotAgingItemDto> rows,
        LotAgingSummaryDto summary,
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Antigüedad lotes");

        var localGeneratedAt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);

        worksheet.Cell(1, 1).SetValue(SanitizeText($"{settings.WarehouseName} - Antigüedad de lotes internos"));
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;

        worksheet.Cell(2, 1).SetValue(SanitizeText(
            $"Generado el: {localGeneratedAt:yyyy-MM-dd HH:mm:ss} ({settings.TimeZoneId}) | Total: {rows.Count} lotes activos"));
        worksheet.Cell(2, 1).Style.Font.Italic = true;

        worksheet.Cell(3, 1).SetValue(SanitizeText(
            "Aviso: La antigüedad refleja los días transcurridos desde el registro del lote en el almacén local (tiempo de permanencia); no representa fecha de caducidad ni vencimiento de fabricante."));
        worksheet.Cell(3, 1).Style.Font.Italic = true;
        worksheet.Cell(3, 1).Style.Font.FontColor = XLColor.FromArgb(100, 116, 139);

        worksheet.Cell(4, 1).SetValue(SanitizeText(
            $"Resumen: 0–30d: {summary.Days0To30LotCount:N0} | 31–60d: {summary.Days31To60LotCount:N0} | 61–90d: {summary.Days61To90LotCount:N0} | Más de 90d: {summary.Days90PlusLotCount:N0} | Filtros: {FormatAnalyticsFilter(filter)}"));
        worksheet.Cell(4, 1).Style.Font.Italic = true;

        string[] headers =
        [
            "SKU", "Descripción", "Unidad", "Lote interno", "Fecha ingreso",
            "Días en almacén", "Rango antigüedad", "Cantidad", "Posiciones", "Ubicación principal"
        ];
        WriteTableHeaders(worksheet, headers, 6);

        var currentRow = 7;
        foreach (var row in rows)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(row.Sku));
            worksheet.Cell(currentRow, 2).SetValue(SanitizeText(row.Description ?? string.Empty));
            worksheet.Cell(currentRow, 3).SetValue(SanitizeText(row.UnitCode));
            worksheet.Cell(currentRow, 4).SetValue(SanitizeText(row.LotNumber));
            if (row.LotDate is not null)
            {
                worksheet.Cell(currentRow, 5).SetValue(row.LotDate.Value.ToDateTime(TimeOnly.MinValue));
                worksheet.Cell(currentRow, 5).Style.DateFormat.Format = "yyyy-mm-dd";
            }
            SetCount(worksheet.Cell(currentRow, 6), row.AgeDays);
            worksheet.Cell(currentRow, 7).SetValue(SanitizeText(row.AgeBucketLabel));
            SetNumber(worksheet.Cell(currentRow, 8), row.Quantity);
            SetCount(worksheet.Cell(currentRow, 9), row.LocationCount);
            worksheet.Cell(currentRow, 10).SetValue(SanitizeText(row.PrimaryLocationCode));
            currentRow++;
        }

        var totalCell = worksheet.Cell(currentRow, 1);
        totalCell.SetValue("Total lotes");
        totalCell.Style.Font.Bold = true;
        SetCount(worksheet.Cell(currentRow, 4), rows.Count, isBold: true);

        worksheet.Range(currentRow, 1, currentRow, 10).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        worksheet.Range(currentRow, 1, currentRow, 10).Style.Fill.BackgroundColor = XLColor.FromArgb(241, 245, 249);

        worksheet.Columns().AdjustToContents(5, Math.Max(6, currentRow));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportLotAgingToCsvAsync(
        IReadOnlyList<LotAgingItemDto> rows,
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var metadata = FormatAnalyticsFilter(filter);

        var sb = new StringBuilder();
        sb.AppendLine("SKU,Descripción,Unidad,Lote interno,Fecha ingreso,Días en almacén,Rango antigüedad,Cantidad,Posiciones,Ubicación principal,Aviso permanencia,Zona horaria,Filtros aplicados");

        const string notice = "Tiempo de permanencia en almacén local; no representa fecha de caducidad ni vencimiento";

        foreach (var row in rows)
        {
            sb.Append(EscapeCsv(SanitizeText(row.Sku))).Append(',')
              .Append(EscapeCsv(SanitizeText(row.Description ?? string.Empty))).Append(',')
              .Append(EscapeCsv(SanitizeText(row.UnitCode))).Append(',')
              .Append(EscapeCsv(SanitizeText(row.LotNumber))).Append(',')
              .Append(EscapeCsv(row.LotDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty)).Append(',')
              .Append(row.AgeDays.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(EscapeCsv(SanitizeText(row.AgeBucketLabel))).Append(',')
              .Append(row.Quantity.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
              .Append(row.LocationCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(EscapeCsv(SanitizeText(row.PrimaryLocationCode))).Append(',')
              .Append(EscapeCsv(notice)).Append(',')
              .Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).Append(',')
              .Append(EscapeCsv(SanitizeText(metadata))).AppendLine();
        }

        return CsvBytes(sb);
    }

    public async Task<byte[]> ExportCoverageToExcelAsync(
        IReadOnlyList<SkuCoverageItemDto> rows,
        SkuCoverageSummaryDto summary,
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Cobertura");

        var localGeneratedAt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);

        worksheet.Cell(1, 1).SetValue(SanitizeText($"{settings.WarehouseName} - Cobertura estimada por consumo"));
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;

        worksheet.Cell(2, 1).SetValue(SanitizeText(
            $"Generado el: {localGeneratedAt:yyyy-MM-dd HH:mm:ss} ({settings.TimeZoneId}) | Total: {rows.Count} productos evaluados"));
        worksheet.Cell(2, 1).Style.Font.Italic = true;

        worksheet.Cell(3, 1).SetValue(SanitizeText(
            $"Alertas: Críticos: {summary.CriticalCount:N0} | Bajos: {summary.LowCount:N0} | Normales: {summary.NormalCount:N0} | Exceso: {summary.ExcessCount:N0} | Sin consumo: {summary.NoRecentConsumptionCount:N0} | Agotados: {summary.ExhaustedCount:N0}"));
        worksheet.Cell(3, 1).Style.Font.Italic = true;

        worksheet.Cell(4, 1).SetValue(SanitizeText($"Filtros: {FormatAnalyticsFilter(filter)}"));
        worksheet.Cell(4, 1).Style.Font.Italic = true;

        string[] headers =
        [
            "SKU", "Descripción", "Unidad", "Stock disponible (racks)", "Consumo neto período",
            "Consumo diario promedio", "Días cobertura", "Clasificación", "Última salida"
        ];
        WriteTableHeaders(worksheet, headers, 6);

        var currentRow = 7;
        foreach (var row in rows)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(row.Sku));
            worksheet.Cell(currentRow, 2).SetValue(SanitizeText(row.Description ?? string.Empty));
            worksheet.Cell(currentRow, 3).SetValue(SanitizeText(row.UnitCode));
            SetNumber(worksheet.Cell(currentRow, 4), row.AvailableStock);
            SetNumber(worksheet.Cell(currentRow, 5), row.NetConsumption);
            SetNumber(worksheet.Cell(currentRow, 6), row.DailyAverageConsumption);

            if (row.CoverageDays is null)
            {
                worksheet.Cell(currentRow, 7).SetValue("Sin consumo reciente");
            }
            else
            {
                worksheet.Cell(currentRow, 7).SetValue(row.CoverageDays.Value);
                worksheet.Cell(currentRow, 7).Style.NumberFormat.Format = "#,##0.0";
            }

            worksheet.Cell(currentRow, 8).SetValue(SanitizeText(row.ClassificationLabel));
            SetLocalDate(worksheet.Cell(currentRow, 9), row.LastExitDateUtc, timeZone);
            currentRow++;
        }

        var totalCell = worksheet.Cell(currentRow, 1);
        totalCell.SetValue("Total productos");
        totalCell.Style.Font.Bold = true;
        SetCount(worksheet.Cell(currentRow, 4), rows.Count, isBold: true);

        worksheet.Range(currentRow, 1, currentRow, 9).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        worksheet.Range(currentRow, 1, currentRow, 9).Style.Fill.BackgroundColor = XLColor.FromArgb(241, 245, 249);

        worksheet.Columns().AdjustToContents(5, Math.Max(6, currentRow));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportCoverageToCsvAsync(
        IReadOnlyList<SkuCoverageItemDto> rows,
        InventoryAnalyticsFilter filter,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var metadata = FormatAnalyticsFilter(filter);

        var sb = new StringBuilder();
        sb.AppendLine("SKU,Descripción,Unidad,Stock disponible (racks),Consumo neto período,Consumo diario promedio,Días cobertura,Clasificación,Última salida,Zona horaria,Filtros aplicados");

        foreach (var row in rows)
        {
            sb.Append(EscapeCsv(SanitizeText(row.Sku))).Append(',')
              .Append(EscapeCsv(SanitizeText(row.Description ?? string.Empty))).Append(',')
              .Append(EscapeCsv(SanitizeText(row.UnitCode))).Append(',')
              .Append(row.AvailableStock.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
              .Append(row.NetConsumption.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
              .Append(row.DailyAverageConsumption.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',')
              .Append(EscapeCsv(row.CoverageDays?.ToString("0.0", CultureInfo.InvariantCulture) ?? "Sin consumo reciente")).Append(',')
              .Append(EscapeCsv(SanitizeText(row.ClassificationLabel))).Append(',')
              .Append(EscapeCsv(FormatLocalDate(row.LastExitDateUtc, timeZone))).Append(',')
              .Append(EscapeCsv(SanitizeText(settings.TimeZoneId))).Append(',')
              .Append(EscapeCsv(SanitizeText(metadata))).AppendLine();
        }

        return CsvBytes(sb);
    }

    // =========================================================================
    // EXPORTACIONES ETAPA 4: CARGA DE TRABAJO, MAPA DE CALOR Y RESUMEN EJECUTIVO
    // =========================================================================

    public async Task<byte[]> ExportWorkloadToExcelAsync(
        IReadOnlyList<WorkloadOperatorDto> operators,
        WorkloadSummaryDto summary,
        WorkloadReportFilter filter,
        string periodLabel,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Carga de Trabajo");

        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        worksheet.Cell(1, 1).SetValue(SanitizeText($"{settings.WarehouseName} - Reporte de Actividad y Carga de Trabajo por Responsable"));
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;

        worksheet.Cell(2, 1).SetValue(SanitizeText($"Generado el: {localNow:yyyy-MM-dd HH:mm:ss} ({settings.TimeZoneId}) | Período: {periodLabel} | {WorkloadReportService.FormatShiftLabel(filter.Shift)}"));
        worksheet.Cell(2, 1).Style.Font.Italic = true;

        worksheet.Cell(3, 1).SetValue(SanitizeText($"Resumen: {summary.TotalOperations} operaciones efectivas | {summary.TotalLines} líneas | {summary.ActiveOperatorsCount} operadores activos | Mayor actividad: {summary.TopOperatorName ?? "Ninguno"} ({summary.TopOperatorOperations} ops)"));
        worksheet.Cell(3, 1).Style.Font.Bold = true;

        string[] headers =
        [
            "Responsable",
            "Rol",
            "Operaciones totales",
            "Líneas totales",
            "Entradas (Ops)",
            "Entradas (Líneas)",
            "Salidas (Ops)",
            "Salidas (Líneas)",
            "Transferencias (Ops)",
            "Transferencias (Líneas)",
            "Ajustes (Ops)",
            "Ajustes (Líneas)",
            "Primera actividad",
            "Última actividad"
        ];

        var startRow = 5;
        WriteTableHeaders(worksheet, headers, startRow);

        var currentRow = startRow + 1;
        foreach (var op in operators)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(op.FullName));
            worksheet.Cell(currentRow, 2).SetValue(SanitizeText(op.RoleName));
            SetCount(worksheet.Cell(currentRow, 3), op.TotalOperations, isBold: true);
            SetCount(worksheet.Cell(currentRow, 4), op.TotalLines, isBold: true);
            SetCount(worksheet.Cell(currentRow, 5), op.EntryOperations);
            SetCount(worksheet.Cell(currentRow, 6), op.EntryLines);
            SetCount(worksheet.Cell(currentRow, 7), op.ExitOperations);
            SetCount(worksheet.Cell(currentRow, 8), op.ExitLines);
            SetCount(worksheet.Cell(currentRow, 9), op.TransferOperations);
            SetCount(worksheet.Cell(currentRow, 10), op.TransferLines);
            SetCount(worksheet.Cell(currentRow, 11), op.AdjustmentOperations);
            SetCount(worksheet.Cell(currentRow, 12), op.AdjustmentLines);
            SetLocalDate(worksheet.Cell(currentRow, 13), op.FirstActivityLocal, timeZone);
            SetLocalDate(worksheet.Cell(currentRow, 14), op.LastActivityLocal, timeZone);
            currentRow++;
        }

        // Fila de totales
        if (operators.Count > 0)
        {
            worksheet.Cell(currentRow, 1).SetValue("TOTALES");
            worksheet.Cell(currentRow, 1).Style.Font.Bold = true;
            for (var col = 3; col <= 12; col++)
            {
                var colLetter = XLHelper.GetColumnLetterFromNumber(col);
                var cell = worksheet.Cell(currentRow, col);
                cell.FormulaA1 = $"SUM({colLetter}{startRow + 1}:{colLetter}{currentRow - 1})";
                cell.Style.Font.Bold = true;
                cell.Style.NumberFormat.Format = "#,##0";
            }
            worksheet.Range(currentRow, 1, currentRow, headers.Length).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            worksheet.Range(currentRow, 1, currentRow, headers.Length).Style.Fill.BackgroundColor = XLColor.FromArgb(241, 245, 249);
        }

        worksheet.Columns().AdjustToContents();
        worksheet.SheetView.FreezeRows(startRow);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportWorkloadToCsvAsync(
        IReadOnlyList<WorkloadOperatorDto> operators,
        WorkloadSummaryDto summary,
        WorkloadReportFilter filter,
        string periodLabel,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);

        var sb = new StringBuilder();
        sb.AppendLine($"# {settings.WarehouseName} - Reporte de Actividad y Carga de Trabajo");
        sb.AppendLine($"# Generado el: {localNow:yyyy-MM-dd HH:mm:ss} ({settings.TimeZoneId}) | Período: {periodLabel} | {WorkloadReportService.FormatShiftLabel(filter.Shift)}");
        sb.AppendLine($"# Resumen: {summary.TotalOperations} operaciones | {summary.TotalLines} líneas | {summary.ActiveOperatorsCount} operadores activos | Mayor actividad: {summary.TopOperatorName ?? "Ninguno"} ({summary.TopOperatorOperations} ops)");
        sb.AppendLine("Responsable,Rol,Operaciones totales,Líneas totales,Entradas Ops,Entradas Líneas,Salidas Ops,Salidas Líneas,Transferencias Ops,Transferencias Líneas,Ajustes Ops,Ajustes Líneas,Primera actividad,Última actividad");

        foreach (var op in operators)
        {
            sb.Append(EscapeCsv(SanitizeText(op.FullName))).Append(',')
              .Append(EscapeCsv(SanitizeText(op.RoleName))).Append(',')
              .Append(op.TotalOperations).Append(',')
              .Append(op.TotalLines).Append(',')
              .Append(op.EntryOperations).Append(',')
              .Append(op.EntryLines).Append(',')
              .Append(op.ExitOperations).Append(',')
              .Append(op.ExitLines).Append(',')
              .Append(op.TransferOperations).Append(',')
              .Append(op.TransferLines).Append(',')
              .Append(op.AdjustmentOperations).Append(',')
              .Append(op.AdjustmentLines).Append(',')
              .Append(EscapeCsv(FormatLocalDate(op.FirstActivityLocal, timeZone))).Append(',')
              .Append(EscapeCsv(FormatLocalDate(op.LastActivityLocal, timeZone))).AppendLine();
        }

        return CsvBytes(sb);
    }

    public async Task<byte[]> ExportHeatmapToExcelAsync(
        IReadOnlyList<RackHeatmapItemDto> racks,
        HeatmapSummaryDto summary,
        HeatmapReportFilter filter,
        string periodLabel,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Mapa de Calor");

        var metricName = filter.Metric == HeatmapMetricType.AccessFrequency ? "Frecuencia de Accesos / Picking" : "Densidad de Ocupación";
        worksheet.Cell(1, 1).SetValue(SanitizeText($"{settings.WarehouseName} - Mapa de Calor ({metricName})"));
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;

        worksheet.Cell(2, 1).SetValue(SanitizeText($"Generado el: {localNow:yyyy-MM-dd HH:mm:ss} ({settings.TimeZoneId}) | Período: {periodLabel} | Métrica: {metricName}"));
        worksheet.Cell(2, 1).Style.Font.Italic = true;

        worksheet.Cell(3, 1).SetValue(SanitizeText($"Resumen: {summary.TotalRacks} racks evaluados | {summary.ActiveRacks} racks activos | Máx. accesos: {summary.MaxAccessCount} | Ocupación promedio: {summary.AverageOccupancyPercent:0.00}% | Racks de alta intensidad (Nivel 3+): {summary.HighHeatRacksCount}"));
        worksheet.Cell(3, 1).Style.Font.Bold = true;

        string[] headers =
        [
            "Fila",
            "Rack",
            "Etiqueta",
            "Accesos (Movimientos)",
            "Posiciones totales",
            "Posiciones ocupadas",
            "Ocupación %",
            "Nivel térmico (0-4)",
            "Clasificación"
        ];

        var startRow = 5;
        WriteTableHeaders(worksheet, headers, startRow);

        var currentRow = startRow + 1;
        foreach (var rack in racks)
        {
            worksheet.Cell(currentRow, 1).SetValue(SanitizeText(rack.RowCode));
            worksheet.Cell(currentRow, 2).SetValue(rack.RackNumber ?? 0);
            worksheet.Cell(currentRow, 3).SetValue(SanitizeText(rack.Label));
            SetCount(worksheet.Cell(currentRow, 4), rack.AccessCount);
            SetCount(worksheet.Cell(currentRow, 5), rack.TotalPositions);
            SetCount(worksheet.Cell(currentRow, 6), rack.OccupiedPositions);
            SetPercentage(worksheet.Cell(currentRow, 7), rack.OccupancyPercent);
            SetCount(worksheet.Cell(currentRow, 8), rack.HeatLevel);

            var classLabel = rack.HeatLevel switch
            {
                0 => "Sin actividad / Vacío",
                1 => "Baja (≤ 25%)",
                2 => "Moderada (26-50%)",
                3 => "Alta (51-75%)",
                _ => "Crítica (> 75%)"
            };
            worksheet.Cell(currentRow, 9).SetValue(classLabel);
            currentRow++;
        }

        // Totales
        if (racks.Count > 0)
        {
            worksheet.Cell(currentRow, 1).SetValue("TOTALES");
            worksheet.Cell(currentRow, 1).Style.Font.Bold = true;
            for (var col = 4; col <= 6; col++)
            {
                var colLetter = XLHelper.GetColumnLetterFromNumber(col);
                var cell = worksheet.Cell(currentRow, col);
                cell.FormulaA1 = $"SUM({colLetter}{startRow + 1}:{colLetter}{currentRow - 1})";
                cell.Style.Font.Bold = true;
                cell.Style.NumberFormat.Format = "#,##0";
            }
            worksheet.Range(currentRow, 1, currentRow, headers.Length).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            worksheet.Range(currentRow, 1, currentRow, headers.Length).Style.Fill.BackgroundColor = XLColor.FromArgb(241, 245, 249);
        }

        worksheet.Columns().AdjustToContents();
        worksheet.SheetView.FreezeRows(startRow);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<byte[]> ExportHeatmapToCsvAsync(
        IReadOnlyList<RackHeatmapItemDto> racks,
        HeatmapSummaryDto summary,
        HeatmapReportFilter filter,
        string periodLabel,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var metricName = filter.Metric == HeatmapMetricType.AccessFrequency ? "Frecuencia de Accesos" : "Densidad de Ocupación";

        var sb = new StringBuilder();
        sb.AppendLine($"# {settings.WarehouseName} - Mapa de Calor ({metricName})");
        sb.AppendLine($"# Generado el: {localNow:yyyy-MM-dd HH:mm:ss} ({settings.TimeZoneId}) | Período: {periodLabel} | Métrica: {metricName}");
        sb.AppendLine($"# Resumen: {summary.TotalRacks} racks evaluados | {summary.ActiveRacks} racks activos | Máx. accesos: {summary.MaxAccessCount} | Ocupación promedio: {summary.AverageOccupancyPercent:0.00}% | Racks Nivel 3+: {summary.HighHeatRacksCount}");
        sb.AppendLine("Fila,Rack,Etiqueta,Accesos,Posiciones totales,Posiciones ocupadas,Ocupación %,Nivel térmico,Clasificación");

        foreach (var rack in racks)
        {
            var classLabel = rack.HeatLevel switch
            {
                0 => "Sin actividad / Vacío",
                1 => "Baja (≤ 25%)",
                2 => "Moderada (26-50%)",
                3 => "Alta (51-75%)",
                _ => "Crítica (> 75%)"
            };

            sb.Append(EscapeCsv(SanitizeText(rack.RowCode))).Append(',')
              .Append(rack.RackNumber?.ToString() ?? string.Empty).Append(',')
              .Append(EscapeCsv(SanitizeText(rack.Label))).Append(',')
              .Append(rack.AccessCount).Append(',')
              .Append(rack.TotalPositions).Append(',')
              .Append(rack.OccupiedPositions).Append(',')
              .Append((rack.OccupancyPercent / 100m).ToString("0.00%", CultureInfo.InvariantCulture)).Append(',')
              .Append(rack.HeatLevel).Append(',')
              .Append(EscapeCsv(classLabel)).AppendLine();
        }

        return CsvBytes(sb);
    }

    public async Task<byte[]> ExportExecutiveToExcelAsync(
        ExecutiveReportDto report,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Resumen Ejecutivo");

        worksheet.Cell(1, 1).SetValue(SanitizeText($"{settings.WarehouseName} - Informe Ejecutivo de Almacén"));
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 16;
        worksheet.Cell(1, 1).Style.Font.FontColor = XLColor.FromArgb(15, 23, 42);

        worksheet.Cell(2, 1).SetValue(SanitizeText($"Situación actual al {report.GeneratedAtLocal:dd/MM/yyyy HH:mm} ({report.TimeZoneId})"));
        worksheet.Cell(2, 1).Style.Font.Italic = true;
        worksheet.Cell(3, 1).SetValue(SanitizeText(
            $"Actividad del {report.ActivityFrom:dd/MM/yyyy} al {report.ActivityTo:dd/MM/yyyy} ({report.PeriodLabel}) | " +
            $"Comparación del {report.PreviousActivityFrom:dd/MM/yyyy} al {report.PreviousActivityTo:dd/MM/yyyy} ({report.PreviousPeriodLabel})"));
        worksheet.Cell(3, 1).Style.Font.Italic = true;

        worksheet.Cell(5, 1).SetValue("1. REQUIERE ATENCIÓN AHORA");
        worksheet.Cell(5, 1).Style.Font.Bold = true;
        worksheet.Cell(5, 1).Style.Font.FontSize = 12;
        worksheet.Cell(6, 1).SetValue("Cobertura crítica según consumo efectivo de 30 días:");
        SetCount(worksheet.Cell(6, 2), report.InventoryHealth.CriticalCoverageSkus, isBold: true);
        worksheet.Cell(7, 1).SetValue("Posiciones con saldo negativo:");
        SetCount(worksheet.Cell(7, 2), report.Capacity.NegativePositions, isBold: true);
        worksheet.Cell(8, 1).SetValue("SKU debajo del mínimo:");
        SetCount(worksheet.Cell(8, 2), report.InventoryHealth.LowStockSkus, isBold: true);
        worksheet.Cell(9, 1).SetValue("Las alertas pueden contener el mismo SKU y no deben sumarse.");
        worksheet.Cell(9, 1).Style.Font.Italic = true;

        worksheet.Cell(11, 1).SetValue("2. SITUACIÓN ACTUAL DEL INVENTARIO");
        worksheet.Cell(11, 1).Style.Font.Bold = true;
        worksheet.Cell(11, 1).Style.Font.FontSize = 12;

        worksheet.Cell(12, 1).SetValue("SKU activos:");
        SetCount(worksheet.Cell(12, 2), report.InventoryHealth.TotalActiveSkus, isBold: true);
        worksheet.Cell(13, 1).SetValue("SKU debajo del mínimo:");
        SetCount(worksheet.Cell(13, 2), report.InventoryHealth.LowStockSkus);
        worksheet.Cell(14, 1).SetValue("SKU con cobertura crítica:");
        SetCount(worksheet.Cell(14, 2), report.InventoryHealth.CriticalCoverageSkus);
        worksheet.Cell(15, 1).SetValue("SKU con existencia y sin salida efectiva durante 90 días o más:");
        SetCount(worksheet.Cell(15, 2), report.InventoryHealth.Stagnant90PlusSkus);

        worksheet.Cell(17, 1).SetValue("3. CAPACIDAD ACTUAL DEL ALMACÉN");
        worksheet.Cell(17, 1).Style.Font.Bold = true;
        worksheet.Cell(17, 1).Style.Font.FontSize = 12;

        worksheet.Cell(18, 1).SetValue("Posiciones totales:");
        SetCount(worksheet.Cell(18, 2), report.Capacity.TotalRackPositions);
        worksheet.Cell(19, 1).SetValue("Bloqueadas:");
        SetCount(worksheet.Cell(19, 2), report.Capacity.BlockedPositions);
        worksheet.Cell(20, 1).SetValue("Con saldo negativo:");
        SetCount(worksheet.Cell(20, 2), report.Capacity.NegativePositions);
        worksheet.Cell(21, 1).SetValue("Ocupadas:");
        SetCount(worksheet.Cell(21, 2), report.Capacity.OccupiedPositions);
        worksheet.Cell(22, 1).SetValue("Vacías disponibles:");
        SetCount(worksheet.Cell(22, 2), report.Capacity.EmptyPositions);
        worksheet.Cell(23, 1).SetValue("Utilización del espacio utilizable:");
        SetPercentage(worksheet.Cell(23, 2), report.Capacity.UtilizationPercent, isBold: true);
        worksheet.Cell(24, 1).SetValue("Clasificación exclusiva: bloqueada, negativa, ocupada o vacía. La utilización excluye las bloqueadas.");
        worksheet.Cell(24, 1).Style.Font.Italic = true;

        worksheet.Cell(26, 1).SetValue("4. ACTIVIDAD DEL PERÍODO Y COMPARACIÓN");
        worksheet.Cell(26, 1).Style.Font.Bold = true;
        worksheet.Cell(26, 1).Style.Font.FontSize = 12;

        string[] flowHeaders =
        [
            "Actividad", "Movimientos actuales", "Detalles actuales", "Movimientos anteriores", "Detalles anteriores",
            "Diferencia movimientos", "Variación movimientos", "Diferencia detalles", "Variación detalles"
        ];
        WriteTableHeaders(worksheet, flowHeaders, 27);

        WriteFlowRow(28, "Total", report.OperationalFlow.TotalMovements, report.OperationalFlow.TotalDetails,
            report.Comparison.TotalMovements, report.Comparison.TotalDetails);
        WriteFlowRow(29, "Entradas", report.OperationalFlow.EntryMovements, report.OperationalFlow.EntryDetails,
            report.Comparison.EntryMovements, null);
        WriteFlowRow(30, "Salidas", report.OperationalFlow.ExitMovements, report.OperationalFlow.ExitDetails,
            report.Comparison.ExitMovements, null);
        WriteFlowRow(31, "Transferencias", report.OperationalFlow.TransferMovements, report.OperationalFlow.TransferDetails,
            report.Comparison.TransferMovements, null);
        WriteFlowRow(32, "Ajustes", report.OperationalFlow.AdjustmentMovements, report.OperationalFlow.AdjustmentDetails,
            report.Comparison.AdjustmentMovements, null);

        worksheet.Cell(34, 1).SetValue("5. PRODUCTOS CON MÁS SALIDAS");
        worksheet.Cell(34, 1).Style.Font.Bold = true;
        worksheet.Cell(34, 1).Style.Font.FontSize = 12;

        string[] topHeaders = ["SKU", "Descripción", "Unidad", "Cantidad salida", "Movimientos"];
        WriteTableHeaders(worksheet, topHeaders, 35);

        var topRow = 36;
        foreach (var sku in report.TopDemandedSkus)
        {
            worksheet.Cell(topRow, 1).SetValue(SanitizeText(sku.Sku));
            worksheet.Cell(topRow, 2).SetValue(SanitizeText(sku.Description ?? string.Empty));
            worksheet.Cell(topRow, 3).SetValue(SanitizeText(sku.Unit));
            SetNumber(worksheet.Cell(topRow, 4), sku.TotalQuantity);
            SetCount(worksheet.Cell(topRow, 5), sku.MovementCount);
            topRow++;
        }

        var stagHeaderRow = topRow + 1;
        worksheet.Cell(stagHeaderRow, 1).SetValue("6. PRODUCTOS CON EXISTENCIA Y SIN SALIDA RECIENTE");
        worksheet.Cell(stagHeaderRow, 1).Style.Font.Bold = true;
        worksheet.Cell(stagHeaderRow, 1).Style.Font.FontSize = 12;

        string[] stagHeaders = ["SKU", "Descripción", "Unidad", "Existencia actual", "Días sin salida"];
        WriteTableHeaders(worksheet, stagHeaders, stagHeaderRow + 1);

        var stagRow = stagHeaderRow + 2;
        foreach (var sku in report.StagnantSkus)
        {
            worksheet.Cell(stagRow, 1).SetValue(SanitizeText(sku.Sku));
            worksheet.Cell(stagRow, 2).SetValue(SanitizeText(sku.Description ?? string.Empty));
            worksheet.Cell(stagRow, 3).SetValue(SanitizeText(sku.Unit));
            SetNumber(worksheet.Cell(stagRow, 4), sku.CurrentStock);
            if (sku.DaysWithoutExit.HasValue)
                SetCount(worksheet.Cell(stagRow, 5), sku.DaysWithoutExit.Value);
            else
                worksheet.Cell(stagRow, 5).SetValue("Nunca salió");
            stagRow++;
        }

        worksheet.Cell(stagRow + 1, 1).SetValue("7. CÓMO SE CALCULA");
        worksheet.Cell(stagRow + 1, 1).Style.Font.Bold = true;
        worksheet.Cell(stagRow + 2, 1).SetValue("Un movimiento es una confirmación; cada producto incluido representa un detalle. Una transferencia con tres productos equivale a un movimiento y tres detalles.");
        worksheet.Cell(stagRow + 3, 1).SetValue("Se cuentan movimientos efectivos: se excluyen originales corregidos y reversos; se incluyen reemplazos vigentes.");
        worksheet.Cell(stagRow + 4, 1).SetValue("Los días sin salida se miden desde la última salida efectiva y no representan edad física del inventario.");

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();

        void WriteFlowRow(
            int row,
            string label,
            int currentMovements,
            int currentDetails,
            MetricComparisonDto movementComparison,
            MetricComparisonDto? detailComparison)
        {
            worksheet.Cell(row, 1).SetValue(label);
            SetCount(worksheet.Cell(row, 2), currentMovements, label == "Total");
            SetCount(worksheet.Cell(row, 3), currentDetails, label == "Total");
            SetCount(worksheet.Cell(row, 4), movementComparison.Previous);
            if (detailComparison is not null)
                SetCount(worksheet.Cell(row, 5), detailComparison.Previous);
            SetCount(worksheet.Cell(row, 6), movementComparison.Delta);
            if (movementComparison.PercentChange is decimal percentage)
                SetPercentage(worksheet.Cell(row, 7), percentage);
            else
                worksheet.Cell(row, 7).SetValue("Sin base anterior");
            if (detailComparison is not null)
            {
                SetCount(worksheet.Cell(row, 8), detailComparison.Delta);
                if (detailComparison.PercentChange is decimal detailPercentage)
                    SetPercentage(worksheet.Cell(row, 9), detailPercentage);
                else
                    worksheet.Cell(row, 9).SetValue("Sin base anterior");
            }
        }
    }


    private static void WriteInventoryHeader(
        IXLWorksheet worksheet,
        string title,
        int totalRows,
        string timeZoneId,
        string filterDescription,
        TimeZoneInfo timeZone,
        string totalLabel = "productos")
    {
        worksheet.Cell(1, 1).SetValue(SanitizeText(title));
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        worksheet.Cell(2, 1).SetValue(SanitizeText(
            $"Generado el: {localNow:yyyy-MM-dd HH:mm:ss} ({timeZoneId}) | Total: {totalRows} {totalLabel}"));
        worksheet.Cell(2, 1).Style.Font.Italic = true;
        worksheet.Cell(3, 1).SetValue(SanitizeText($"Filtros: {filterDescription}"));
        worksheet.Cell(3, 1).Style.Font.Italic = true;
    }

    private static void WriteTableHeaders(IXLWorksheet worksheet, IReadOnlyList<string> headers, int rowNumber = 5)
    {
        for (var column = 0; column < headers.Count; column++)
        {
            var cell = worksheet.Cell(rowNumber, column + 1);
            cell.SetValue(headers[column]);
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(15, 23, 42);
        }
    }

    private static void SetNumber(IXLCell cell, decimal value)
    {
        cell.SetValue(value);
        cell.Style.NumberFormat.Format = "#,##0.0000";
    }

    private static void SetCount(IXLCell cell, int value, bool isBold = false)
    {
        cell.SetValue(value);
        cell.Style.NumberFormat.Format = "#,##0";
        if (isBold)
            cell.Style.Font.Bold = true;
    }

    private static void SetPercentage(IXLCell cell, decimal percentageScale100, bool isBold = false)
    {
        cell.SetValue(percentageScale100 / 100m);
        cell.Style.NumberFormat.Format = "0.00%";
        if (isBold)
            cell.Style.Font.Bold = true;
    }

    private static void SetLocalDate(IXLCell cell, DateTimeOffset? value, TimeZoneInfo timeZone)
    {
        if (value is null)
            return;
        cell.SetValue(TimeZoneInfo.ConvertTime(value.Value, timeZone).DateTime);
        cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
    }

    private static string FormatLocalDate(DateTimeOffset? value, TimeZoneInfo timeZone) =>
        value is null ? string.Empty : TimeZoneInfo.ConvertTime(value.Value, timeZone).ToString("yyyy-MM-dd HH:mm:ss");

    private static byte[] CsvBytes(StringBuilder value)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(value.ToString());
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

    private static string FormatAnalyticsFilter(InventoryAnalyticsFilter filter)
    {
        var values = new List<string> { $"estado={filter.ProductStatus}" };
        if (filter.FromUtc is not null) values.Add($"desde UTC {filter.FromUtc:yyyy-MM-dd HH:mm:ss}");
        if (filter.ToUtc is not null) values.Add($"hasta UTC exclusivo {filter.ToUtc:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(filter.Search)) values.Add($"búsqueda={filter.Search}");
        if (filter.UnitId is not null) values.Add($"unidad={filter.UnitId}");
        if (filter.StagnantCategory is not null) values.Add($"categoría={FormatStagnantCategory(filter.StagnantCategory.Value)}");
        if (filter.AgeBucket is not null && filter.AgeBucket.Value != LotAgeBucket.All) values.Add($"antigüedad={InventoryAnalyticsService.FormatLotAgeBucket(filter.AgeBucket.Value)}");
        if (filter.CoverageClassification is not null && filter.CoverageClassification.Value != CoverageClassification.All) values.Add($"clasificación={InventoryAnalyticsService.FormatCoverageClassification(filter.CoverageClassification.Value)}");
        return string.Join(" | ", values);
    }

    private static string FormatExceptionFilter(string? search) =>
        string.IsNullOrWhiteSpace(search) ? "Sin filtro" : $"búsqueda={search.Trim()}";

    private static string FormatHistoryFilter(InventoryHistoryFilter filter)
    {
        var values = new List<string> { $"estado={filter.State}" };
        if (filter.From is not null) values.Add($"desde UTC {filter.From:yyyy-MM-dd HH:mm:ss}");
        if (filter.To is not null) values.Add($"hasta UTC exclusivo {filter.To:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(filter.Search)) values.Add($"búsqueda={filter.Search}");
        if (!string.IsNullOrWhiteSpace(filter.ProductSearch)) values.Add($"producto={filter.ProductSearch}");
        if (!string.IsNullOrWhiteSpace(filter.LocationSearch)) values.Add($"ubicación={filter.LocationSearch}");
        if (filter.Type is not null) values.Add($"tipo={filter.Type}");
        if (filter.Purpose is not null) values.Add($"propósito={filter.Purpose}");
        if (filter.ResponsibleUserId is not null) values.Add($"responsable={filter.ResponsibleUserId}");
        return string.Join(" | ", values);
    }

    private static string FormatProductState(bool isActive) => isActive ? "Activo" : "Inactivo";

    private static string FormatStagnantCategory(StagnantCategory category) => category switch
    {
        StagnantCategory.Days30To59 => "30-59 días",
        StagnantCategory.Days60To89 => "60-89 días",
        StagnantCategory.Days90Plus => "90+ días",
        StagnantCategory.NeverExited => "Nunca salió",
        _ => category.ToString()
    };

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
        InventoryMovementPurpose.WipConsumption => "Consumo WIP",
        InventoryMovementPurpose.WipSupplierReturn => "Devolución WIP a proveedor",
        InventoryMovementPurpose.CycleCountAdjustment => "Conteo cíclico",
        _ => purpose.ToString()
    };
}
