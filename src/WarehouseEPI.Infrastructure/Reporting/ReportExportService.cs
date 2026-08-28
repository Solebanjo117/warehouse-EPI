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

    private static void WriteTableHeaders(IXLWorksheet worksheet, IReadOnlyList<string> headers)
    {
        for (var column = 0; column < headers.Count; column++)
        {
            var cell = worksheet.Cell(5, column + 1);
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
