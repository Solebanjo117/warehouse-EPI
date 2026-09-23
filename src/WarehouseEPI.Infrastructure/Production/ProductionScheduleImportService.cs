using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public enum ProductionScheduleImportIssueKind
{
    General,
    Configuration,
    UnknownSku,
    UnknownArea,
    UnknownShift,
    InvalidDate,
    InvalidQuantity,
    OpeningReconciliation
}

public enum ProductionScheduleImportTable
{
    Plan,
    Execution,
    Carryover
}

// Value keeps the worksheet text that failed to resolve so the UI can offer a link for it.
public sealed record ProductionScheduleImportIssue(string Sheet, int? Row, string Message,
    ProductionScheduleImportIssueKind Kind = ProductionScheduleImportIssueKind.General,
    ProductionScheduleImportTable? Table = null, string? Value = null);
public sealed record ProductionScheduleImportRowFix(string Sheet, ProductionScheduleImportTable Table, int Row,
    bool Skip = false, decimal? Quantity = null, DateOnly? Date = null);
// Per-import answers to blockers: text links are keyed by worksheet text and never become catalog aliases.
public sealed record ProductionScheduleImportResolutions(
    IReadOnlyDictionary<string, ProductionDailyArea> Areas,
    IReadOnlyDictionary<string, int> Shifts,
    IReadOnlyDictionary<string, Guid> Products,
    IReadOnlyList<ProductionScheduleImportRowFix> Rows)
{
    public static ProductionScheduleImportResolutions None { get; } = new(
        new Dictionary<string, ProductionDailyArea>(), new Dictionary<string, int>(), new Dictionary<string, Guid>(), []);
    public bool IsEmpty => Areas.Count == 0 && Shifts.Count == 0 && Products.Count == 0 && Rows.Count == 0 && Opening.Count == 0 && ClosingTable is null;
    public string? ClosingTable { get; init; }
    public IReadOnlyList<ProductionOpeningResolution> Opening { get; init; } = [];
}
public sealed record ProductionScheduleImportLine(DateOnly Date, string Sku, Guid ProductId, decimal Quantity,
    string? Reference1, string? Reference2, string? Reference3, string? Notes, bool IsCarryover, int SourceRow,
    ProductionDailyArea? StartArea = null, string? SourceSheet = null);
public sealed record ProductionScheduleImportCapture(DateOnly Date, ProductionDailyArea Area, Guid ShiftId,
    string Sku, Guid ProductId, decimal Quantity, string? Reporter, string? Notes, int SourceRow);
public sealed record ProductionScheduleImportCarryover(string Sku, Guid ProductId, ProductionDailyArea Area,
    decimal Quantity, int SourceRow);
public sealed record ProductionScheduleImportWeek(string Sheet, DateOnly WeekStart, bool IsDraft,
    IReadOnlyList<ProductionScheduleImportLine> Lines, IReadOnlyList<ProductionScheduleImportCapture> Captures,
    IReadOnlyList<ProductionScheduleImportCarryover> OpeningCarryovers)
{
    public IReadOnlyList<ProductionScheduleImportLine> FinalLines { get; init; } = Lines;
}
public sealed record ProductionScheduleImportPreview(string FileName, string FileHash,
    IReadOnlyList<ProductionScheduleImportWeek> Weeks, IReadOnlyList<ProductionScheduleImportIssue> Issues,
    IReadOnlyList<string> AppliedResolutions)
{
    public bool CanConfirm => Issues.Count == 0 && Weeks.Count > 0;
    public int LineCount => Weeks.Sum(x => x.Lines.Count);
    public int CaptureCount => Weeks.Sum(x => x.Captures.Count);
    public int FinalLineCount => Weeks.Sum(x => x.FinalLines.Count);
    public IReadOnlyList<ProductionOpeningReview> OpeningReview { get; init; } = [];
    public IReadOnlyList<ProductionImportCell> Evidence { get; init; } = [];
    public IReadOnlyList<string> ClosingCandidates { get; init; } = [];
    public string DependencyHash { get; init; } = "";
    public string Fingerprint { get; init; } = "";
}

public sealed partial class ProductionScheduleImportService(WarehouseDbContext db, TimeProvider timeProvider)
{
    private static readonly DateOnly FirstHistoricalWeek = new(2026, 8, 24);
    private static readonly DateOnly DraftWeek = new(2026, 9, 21);

    public Task<ProductionScheduleImportPreview> PreviewAsync(Stream file, string fileName,
        CancellationToken token = default) => PreviewAsync(file, fileName, ProductionScheduleImportResolutions.None, token);

    public async Task<ProductionScheduleImportPreview> PreviewAsync(Stream file, string fileName,
        ProductionScheduleImportResolutions resolutions, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, token);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var issues = new List<ProductionScheduleImportIssue>();
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        if (config.CuttingStageId is null || config.SewingStageId is null || config.ReadyToPackStageId is null ||
            config.Shift1Id is null || config.Shift2Id is null)
            issues.Add(new("Configuración", null, "Configura las tres áreas y T1/T2 antes de importar.",
                ProductionScheduleImportIssueKind.Configuration));
        else
        {
            var stageIds = new[] { config.CuttingStageId.Value, config.SewingStageId.Value, config.ReadyToPackStageId.Value };
            var shiftIds = new[] { config.Shift1Id.Value, config.Shift2Id.Value };
            if (stageIds.Distinct().Count() != 3 || shiftIds.Distinct().Count() != 2 ||
                await db.ProductionStages.CountAsync(x => stageIds.Contains(x.Id) && x.IsActive, token) != 3 ||
                await db.ProductionShifts.CountAsync(x => shiftIds.Contains(x.Id) && x.IsActive, token) != 2)
                issues.Add(new("Configuración", null, "Selecciona tres procesos activos y diferentes.", ProductionScheduleImportIssueKind.Configuration));
        }
        var catalog = await db.Products.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new { x.Id, x.Sku }).ToListAsync(token);
        var context = new ParseContext(config, catalog.ToDictionary(x => NormalizeSku(x.Sku), x => x.Id),
            catalog.ToDictionary(x => x.Id, x => x.Sku), resolutions, issues);
        var weeks = new List<ProductionScheduleImportWeek>();
        OpeningReadResult opening = new([], [], []);
        try
        {
            using var workbook = new XLWorkbook(new MemoryStream(bytes));
            foreach (var sheet in workbook.Worksheets.Where(x => !IsIgnored(x.Name)))
            {
                if (!TryWeekStart(sheet.Name, out var weekStart) || weekStart < FirstHistoricalWeek || weekStart > DraftWeek)
                    continue;
                var planTable = sheet.Tables.FirstOrDefault(IsPlanTable);
                if (planTable is null)
                {
                    issues.Add(new(sheet.Name, null, "No se encontró una tabla de programa con encabezados Day, Part Number y Qty."));
                    continue;
                }
                var lines = ParseLines(sheet.Name, planTable, weekStart, context);
                var execution = sheet.Tables.FirstOrDefault(IsExecutionTable);
                var captures = execution is null
                    ? []
                    : ParseCaptures(sheet.Name, execution, weekStart, context);
                weeks.Add(new(sheet.Name, weekStart, weekStart == DraftWeek, lines, captures, []));
            }
            opening = await ReconcileOpeningAsync(workbook, weeks, context, resolutions, token);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
        {
            issues.Add(new("Archivo", null, "El archivo no es un XLSX válido o está dañado."));
        }
        foreach (var required in new[]
                 {
                     new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 7),
                     new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 21)
                 }.Where(x => weeks.All(w => w.WeekStart != x)))
            issues.Add(new("Archivo", null, $"Falta la semana {required:MM-dd} esperada para la carga inicial."));
        for (var index = 0; index < weeks.Count; index++)
        {
            var week = weeks[index];
            weeks[index] = week with
            {
                FinalLines = week.FinalLines.Select(x => x with { Reference1 = Safe(x.Reference1, 120), Reference2 = Safe(x.Reference2, 120),
                    Reference3 = Safe(x.Reference3, 120), Notes = Safe(x.Notes, 500), SourceSheet = x.SourceSheet ?? week.Sheet }).ToArray(),
                Captures = week.Captures.Select(x => x with { Notes = Safe(x.Notes, 500), Reporter = Safe(x.Reporter, 160) }).ToArray()
            };
        }
        var preview = new ProductionScheduleImportPreview(fileName, hash, weeks.OrderBy(x => x.WeekStart).ToArray(), issues, context.Applied)
        {
            OpeningReview = opening.Reviews, Evidence = opening.Evidence, ClosingCandidates = opening.Candidates,
            DependencyHash = await GetDependencyHashAsync(weeks, token, opening.Reviews.Select(x => x.ProductId))
        };
        return preview with { Fingerprint = CanonicalHash(new { Algorithm = "opening-v1", Preview = preview, Resolutions = resolutions }) };
    }

    public async Task<ProductionDailyCommandResult> ConfirmAsync(ProductionScheduleImportPreview preview,
        Guid operationId, Guid actorUserId, CancellationToken token = default)
    {
        if (!preview.CanConfirm) return new(ProductionDailyCommandStatus.ValidationFailed,
            Errors: preview.Issues.Select(x => $"{x.Sheet}{(x.Row.HasValue ? $" fila {x.Row}" : "")}: {x.Message}").ToArray());
        if (!await db.Users.AnyAsync(x => x.Id == actorUserId && x.IsActive && x.Role.Code == "ADMIN", token))
            return new(ProductionDailyCommandStatus.ValidationFailed, Errors: ["La importación requiere ADMIN."]);
        var existing = await db.ProductionScheduleImportBatches.AsNoTracking()
            .SingleOrDefaultAsync(x => x.FileHash == preview.FileHash || x.OperationId == operationId, token);
        if (existing is not null)
            return existing.FileHash == preview.FileHash && existing.RequestFingerprint == preview.Fingerprint && existing.OperationId == operationId
                ? new(ProductionDailyCommandStatus.Success, existing.Id)
                : new(ProductionDailyCommandStatus.IdempotencyConflict);
        if (!await db.Users.AnyAsync(x => x.Id == actorUserId && x.IsActive && x.Role.Code == "ADMIN", token))
            return new(ProductionDailyCommandStatus.ValidationFailed, Errors: ["La importación requiere ADMIN."]);
        if (await db.ProductionScheduleWeeks.AnyAsync(x => preview.Weeks.Select(w => w.WeekStart).Contains(x.WeekStart), token))
            return new(ProductionDailyCommandStatus.ValidationFailed,
                Errors: ["Ya existe una de las semanas del archivo. La carga inicial no reemplaza datos existentes."]);
        var config = await db.ProductionDailyConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1, token);
        var active = (await db.Products.AsNoTracking().Where(x => x.IsActive).Select(x => x.Id).ToListAsync(token)).ToHashSet();
        var missingSkus = preview.Weeks.SelectMany(week => week.Lines.Select(x => (x.Sku, x.ProductId))
                .Concat(week.Captures.Select(x => (x.Sku, x.ProductId)))
                .Concat(week.OpeningCarryovers.Select(x => (x.Sku, x.ProductId))))
            .Where(x => !active.Contains(x.ProductId)).Select(x => x.Sku).Distinct(StringComparer.Ordinal).ToArray();
        if (missingSkus.Length > 0)
            return new(ProductionDailyCommandStatus.ValidationFailed,
                Errors: missingSkus.Select(sku => $"SKU sin resolver: {sku}.").ToArray());
        if (preview.DependencyHash != await GetDependencyHashAsync(preview.Weeks, token, preview.OpeningReview.Select(x => x.ProductId)))
            return new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: ["La revisión cambió. Vuelve a validar antes de confirmar."]);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token)
            : null;
        try
        {
            var created = new Dictionary<DateOnly, ProductionScheduleWeek>();
            foreach (var source in preview.Weeks)
            {
                var week = new ProductionScheduleWeek
                {
                    OperationId = Derive(operationId, source.WeekStart, "week"),
                    RequestFingerprint = preview.FileHash,
                    WeekStart = source.WeekStart,
                    WeekEnd = source.WeekStart.AddDays(5),
                    Status = source.IsDraft ? ProductionScheduleWeekStatus.Draft : ProductionScheduleWeekStatus.Closed,
                    Origin = ProductionScheduleOrigin.ExcelImport,
                    SourceName = source.Sheet,
                    CreatedByUserId = actorUserId,
                    CreatedAt = timeProvider.GetUtcNow(),
                    ClosedByUserId = source.IsDraft ? null : actorUserId,
                    ClosedAt = source.IsDraft ? null : timeProvider.GetUtcNow()
                };
                var sequence = 0;
                foreach (var line in source.FinalLines)
                    week.Lines.Add(new ProductionScheduleLine
                    {
                        Sequence = ++sequence,
                        PlannedDate = line.Date,
                        ProductId = line.ProductId,
                        Quantity = line.Quantity,
                        OrderReference1 = Safe(line.Reference1, 120),
                        OrderReference2 = Safe(line.Reference2, 120),
                        OrderReference3 = Safe(line.Reference3, 120),
                        Notes = Safe(line.Notes, 500),
                        Origin = ProductionScheduleOrigin.ExcelImport,
                        IsCarryover = line.IsCarryover,
                        StartArea = line.StartArea,
                        SourceSheet = line.SourceSheet ?? source.Sheet,
                        SourceRow = line.SourceRow
                    });
                foreach (var capture in source.Captures)
                    week.Captures.Add(new ProductionDailyCapture
                    {
                        OperationId = Derive(operationId, source.WeekStart, $"capture:{capture.SourceRow}"),
                        RequestFingerprint = preview.FileHash,
                        EffectiveDate = capture.Date,
                        Area = capture.Area,
                        StageId = Stage(config, capture.Area),
                        ShiftId = capture.ShiftId,
                        ProductId = capture.ProductId,
                        Quantity = capture.Quantity,
                        Notes = Safe(capture.Notes, 500),
                        Origin = ProductionScheduleOrigin.ExcelImport,
                        ImportedReporter = Safe(capture.Reporter, 160),
                        SourceSheet = source.Sheet,
                        SourceRow = capture.SourceRow,
                        ResponsibleUserId = actorUserId,
                        RecordedAt = timeProvider.GetUtcNow()
                    });
                db.ProductionScheduleWeeks.Add(week);
                created[source.WeekStart] = week;
            }
            var summary = string.Join('\n', preview.AppliedResolutions.Concat(preview.OpeningReview.Where(x => x.Resolution is not null)
                .Select(x => $"Apertura {x.Sku}: {System.Text.Json.JsonSerializer.Serialize(x.Resolution)}")));
            var batch = new ProductionScheduleImportBatch
            {
                OperationId = operationId,
                RequestFingerprint = preview.Fingerprint,
                FileHash = preview.FileHash,
                FileName = Safe(preview.FileName, 255) ?? "Production Schedule.xlsx",
                ImportedByUserId = actorUserId,
                ImportedAt = timeProvider.GetUtcNow(),
                WeekCount = preview.Weeks.Count,
                LineCount = preview.LineCount,
                CaptureCount = preview.CaptureCount,
                ResolutionSummary = summary
            };
            db.ProductionScheduleImportBatches.Add(batch);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, batch.Id);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(ProductionDailyCommandStatus.ConcurrencyConflict,
                Errors: ["La importación coincidió con datos creados en otra sesión. Recarga la previsualización."]);
        }
    }

    private static List<ProductionScheduleImportLine> ParseLines(string sheet, IXLTable table,
        DateOnly weekStart, ParseContext context)
    {
        const ProductionScheduleImportTable Plan = ProductionScheduleImportTable.Plan;
        var result = new List<ProductionScheduleImportLine>();
        var headers = Headers(table);
        foreach (var row in table.DataRange.Rows())
        {
            var sku = Text(row.Cell(headers["partnumber"]));
            var sourceQuantity = Decimal(row.Cell(headers["qty"]));
            if (string.IsNullOrWhiteSpace(sku) && sourceQuantity is null) continue;
            var number = row.RowNumber();
            var fix = context.Fix(sheet, Plan, number);
            if (fix?.Skip == true) continue;
            var productId = context.Product(sku);
            if (productId is null)
            {
                context.Issue(sheet, number, $"SKU sin resolver: {sku ?? "vacío"}.",
                    ProductionScheduleImportIssueKind.UnknownSku, Plan, sku);
                continue;
            }
            var date = context.FixDate(sheet, Plan, number, fix, Date(row.Cell(Column(headers, "day", "date")), weekStart), weekStart);
            if (!InWeek(date, weekStart))
                context.Issue(sheet, number, "La fecha del programa no corresponde a lunes-sábado de la hoja.",
                    ProductionScheduleImportIssueKind.InvalidDate, Plan);
            var quantity = context.FixQuantity(sheet, Plan, number, fix, sourceQuantity);
            if (!IsPositive(quantity))
                context.Issue(sheet, number, "La cantidad programada debe ser positiva.",
                    ProductionScheduleImportIssueKind.InvalidQuantity, Plan);
            if (date is null || string.IsNullOrWhiteSpace(sku)) continue;
            var type = Optional(row, headers, "tipo", "type");
            result.Add(new(date.Value, sku, productId.Value, quantity ?? 0,
                Optional(row, headers, "ordernumber1", "order1", "order"), Optional(row, headers, "ordernumber2", "order2"),
                Optional(row, headers, "ordernumber3", "order3"), Optional(row, headers, "notes", "comments"),
                Normalize(type ?? "").Contains("arrastre", StringComparison.Ordinal), number));
        }
        return result;
    }

    private static List<ProductionScheduleImportCapture> ParseCaptures(string sheet, IXLTable table,
        DateOnly weekStart, ParseContext context)
    {
        const ProductionScheduleImportTable Execution = ProductionScheduleImportTable.Execution;
        var result = new List<ProductionScheduleImportCapture>();
        var headers = Headers(table);
        foreach (var row in table.DataRange.Rows())
        {
            var sku = Text(row.Cell(headers["partnumber"]));
            var sourceQuantity = Decimal(row.Cell(headers["piecescompleted"]));
            if (string.IsNullOrWhiteSpace(sku) && sourceQuantity is null) continue;
            var number = row.RowNumber();
            var fix = context.Fix(sheet, Execution, number);
            if (fix?.Skip == true) continue;
            var areaText = Text(row.Cell(headers["area"]));
            var shiftText = Text(row.Cell(headers["shift"]));
            var productId = context.Product(sku);
            var area = context.Area(areaText);
            var shiftNumber = context.ShiftNumber(shiftText);
            var shift = shiftNumber switch { 1 => context.Config.Shift1Id, 2 => context.Config.Shift2Id, _ => null };
            var date = context.FixDate(sheet, Execution, number, fix, Date(row.Cell(Column(headers, "day", "date")), weekStart), weekStart);
            var quantity = context.FixQuantity(sheet, Execution, number, fix, sourceQuantity);
            if (productId is null)
                context.Issue(sheet, number, $"SKU sin resolver: {sku ?? "vacío"}.",
                    ProductionScheduleImportIssueKind.UnknownSku, Execution, sku);
            if (area is null)
                context.Issue(sheet, number, $"Área sin resolver: {areaText ?? "vacía"}.",
                    ProductionScheduleImportIssueKind.UnknownArea, Execution, areaText);
            // A recognized Shift 1/2 without its daily configuration is already reported once as "Configuración".
            if (shiftNumber is null)
                context.Issue(sheet, number, $"Turno sin resolver: {shiftText ?? "vacío"}.",
                    ProductionScheduleImportIssueKind.UnknownShift, Execution, shiftText);
            if (!InWeek(date, weekStart))
                context.Issue(sheet, number, "La fecha de ejecución no corresponde a lunes-sábado de la hoja.",
                    ProductionScheduleImportIssueKind.InvalidDate, Execution);
            if (!IsPositive(quantity))
                context.Issue(sheet, number, "Las piezas completadas deben ser positivas.",
                    ProductionScheduleImportIssueKind.InvalidQuantity, Execution);
            if (date is null || area is null || shift is null || !IsPositive(quantity) || productId is null) continue;
            result.Add(new(date.Value, area.Value, shift.Value, sku!, productId.Value, quantity!.Value,
                Optional(row, headers, "reportedby", "name", "operator"), Optional(row, headers, "notes", "comments"), number));
        }
        return result;
    }

    private static bool IsPlanTable(IXLTable table)
    {
        var keys = Headers(table).Keys.ToHashSet();
        return (keys.Contains("day") || keys.Contains("date")) && keys.Contains("partnumber") && keys.Contains("qty") && !keys.Contains("area");
    }
    private static bool IsExecutionTable(IXLTable table)
    {
        var keys = Headers(table).Keys.ToHashSet();
        return (keys.Contains("day") || keys.Contains("date")) && keys.Contains("area") && keys.Contains("shift") &&
               keys.Contains("partnumber") && keys.Contains("piecescompleted");
    }
    private static Dictionary<string, int> Headers(IXLTable table) => table.HeadersRow().Cells()
        .Select((cell, index) => new { Key = ImportHeader(cell.GetString()), Index = index + 1 })
        .Where(x => x.Key.Length > 0).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.First().Index);
    private static int Column(IReadOnlyDictionary<string, int> headers, params string[] names) =>
        headers[names.First(headers.ContainsKey)];
    private static string? Optional(IXLRangeRow row, IReadOnlyDictionary<string, int> headers, params string[] names)
    {
        var key = names.FirstOrDefault(headers.ContainsKey);
        return key is null ? null : Text(row.Cell(headers[key]));
    }
    private static string? Text(IXLCell cell)
    {
        var value = (cell.HasFormula ? cell.CachedValue.ToString(CultureInfo.InvariantCulture) : cell.GetFormattedString()).Trim();
        return value.Length == 0 ? null : value;
    }
    private static decimal? Decimal(IXLCell cell)
    {
        if (cell.HasFormula) return cell.CachedValue.IsNumber ? (decimal)cell.CachedValue.GetNumber() : null;
        if (cell.TryGetValue<decimal>(out var value)) return decimal.Round(value, 4);
        return decimal.TryParse(cell.GetFormattedString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            ? decimal.Round(value, 4) : null;
    }
    private static string ImportHeader(string text) => Normalize(text) switch
    {
        "dia" => "day", "fecha" => "date", "sku" or "numerodeparte" => "partnumber",
        "cantidad" => "qty", "piezascompletadas" => "piecescompleted", "turno" => "shift",
        "reportadopor" => "reportedby", "notas" => "notes", "comentarios" => "comments",
        _ => Normalize(text)
    };
    private static DateOnly? Date(IXLCell cell, DateOnly weekStart)
    {
        if (cell.TryGetValue<DateTime>(out var date)) return DateOnly.FromDateTime(date);
        var text = cell.GetFormattedString().Trim();
        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed;
        var day = Normalize(text) switch
        {
            "monday" or "lunes" or "mon" => DayOfWeek.Monday,
            "tuesday" or "martes" or "tue" => DayOfWeek.Tuesday,
            "wednesday" or "miercoles" or "wed" => DayOfWeek.Wednesday,
            "thursday" or "jueves" or "thu" => DayOfWeek.Thursday,
            "friday" or "viernes" or "fri" => DayOfWeek.Friday,
            "saturday" or "sabado" or "sat" => DayOfWeek.Saturday,
            _ => (DayOfWeek?)null
        };
        return day.HasValue ? weekStart.AddDays((int)day.Value - (int)DayOfWeek.Monday) : null;
    }
    private static ProductionDailyArea? ParseArea(string? value) => Normalize(value ?? "") switch
    {
        "cutting" or "cut" or "corte" => ProductionDailyArea.Cutting,
        "sewing" or "sew" or "costura" => ProductionDailyArea.Sewing,
        "readytopack" or "rtp" or "listoparaempacar" => ProductionDailyArea.ReadyToPack,
        _ => null
    };
    private static int? ShiftNumber(string? value) => Normalize(value ?? "") switch
    {
        "shift1" or "t1" or "turno1" => 1,
        "shift2" or "t2" or "turno2" => 2,
        _ => null
    };
    private static Guid Stage(ProductionDailyConfiguration config, ProductionDailyArea area) => area switch
    {
        ProductionDailyArea.Cutting => config.CuttingStageId!.Value,
        ProductionDailyArea.Sewing => config.SewingStageId!.Value,
        _ => config.ReadyToPackStageId!.Value
    };
    private static bool IsIgnored(string name) => Normalize(name) is "dailytemplate" or "weeklyentrytemplate" or "items";
    private static bool TryWeekStart(string name, out DateOnly start)
    {
        start = default;
        var digits = new string(name.TakeWhile(c => char.IsDigit(c) || c == '-' || c == '/').ToArray());
        var parts = digits.Replace('/', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[0], out var month) && int.TryParse(parts[1], out var day) &&
               TryCreate(month, day, out start);
    }
    private static bool TryCreate(int month, int day, out DateOnly date)
    {
        try { date = new DateOnly(2026, month, day); return true; }
        catch (ArgumentOutOfRangeException) { date = default; return false; }
    }
    private static Guid Derive(Guid operationId, DateOnly week, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId:N}:{week:yyyyMMdd}:{purpose}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
    // SKU punctuation is part of the catalog identity, unlike worksheet headings.
    private static string NormalizeSku(string value) => value.Trim().ToUpperInvariant();

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            .Select(char.ToLowerInvariant).ToArray());
    }
    private static string? Safe(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.Length > max ? value[..max] : value;
    }
    private static bool InWeek(DateOnly? date, DateOnly weekStart) =>
        date is { } value && value >= weekStart && value <= weekStart.AddDays(5);
    private static bool IsPositive(decimal? quantity) =>
        quantity > 0 && decimal.Round(quantity.Value, 4) == quantity.Value;

    // Applies per-import resolutions while parsing and records each one that changed the result for the batch audit.
    private sealed class ParseContext
    {
        private readonly IReadOnlyDictionary<string, Guid> products;
        private readonly IReadOnlyDictionary<Guid, string> skus;
        private readonly Dictionary<string, (string Text, ProductionDailyArea Area)> areas;
        private readonly Dictionary<string, (string Text, int Shift)> shifts;
        private readonly Dictionary<string, (string Text, Guid ProductId)> productLinks;
        private readonly Dictionary<(string Sheet, ProductionScheduleImportTable Table, int Row), ProductionScheduleImportRowFix> rows;
        private readonly List<ProductionScheduleImportIssue> issues;
        private readonly List<string> applied = [];

        public ParseContext(ProductionDailyConfiguration config, IReadOnlyDictionary<string, Guid> products,
            IReadOnlyDictionary<Guid, string> skus, ProductionScheduleImportResolutions resolutions,
            List<ProductionScheduleImportIssue> issues)
        {
            Config = config;
            this.products = products;
            this.skus = skus;
            this.issues = issues;
            areas = Links(resolutions.Areas.Where(x => Enum.IsDefined(x.Value)), Normalize);
            shifts = Links(resolutions.Shifts.Where(x => x.Value is 1 or 2), Normalize);
            productLinks = Links(resolutions.Products, NormalizeSku);
            rows = resolutions.Rows.GroupBy(x => (x.Sheet, x.Table, x.Row)).ToDictionary(x => x.Key, x => x.Last());
        }

        public ProductionDailyConfiguration Config { get; }
        public IReadOnlyList<string> Applied => applied;

        public void Issue(string sheet, int? row, string message, ProductionScheduleImportIssueKind kind,
            ProductionScheduleImportTable table, string? value = null) => issues.Add(new(sheet, row, message, kind, table, value));

        public ProductionScheduleImportRowFix? Fix(string sheet, ProductionScheduleImportTable table, int row)
        {
            if (!rows.TryGetValue((sheet, table, row), out var fix)) return null;
            if (fix.Skip) Note($"{Source(sheet, table, row)}: omitida");
            return fix;
        }

        public Guid? Product(string? sku)
        {
            if (string.IsNullOrWhiteSpace(sku)) return null;
            if (products.TryGetValue(NormalizeSku(sku), out var id)) return id;
            if (!productLinks.TryGetValue(NormalizeSku(sku), out var link) || !skus.TryGetValue(link.ProductId, out var target))
                return null;
            Note($"SKU \"{link.Text}\" → {target}");
            return link.ProductId;
        }

        public ProductionDailyArea? Area(string? text)
        {
            var area = ParseArea(text);
            if (area is not null || text is null || !areas.TryGetValue(Normalize(text), out var link)) return area;
            Note($"Área \"{link.Text}\" → {link.Area switch
            {
                ProductionDailyArea.Cutting => "Corte",
                ProductionDailyArea.Sewing => "Costura",
                _ => "Ready to Pack"
            }}");
            return link.Area;
        }

        public int? ShiftNumber(string? text)
        {
            var shift = ProductionScheduleImportService.ShiftNumber(text);
            if (shift is not null || text is null || !shifts.TryGetValue(Normalize(text), out var link)) return shift;
            Note($"Turno \"{link.Text}\" → T{link.Shift}");
            return link.Shift;
        }

        public DateOnly? FixDate(string sheet, ProductionScheduleImportTable table, int row,
            ProductionScheduleImportRowFix? fix, DateOnly? source, DateOnly weekStart)
        {
            if (fix?.Date is not { } date) return source;
            if (InWeek(date, weekStart)) Note($"{Source(sheet, table, row)}: fecha {date:yyyy-MM-dd}");
            return date;
        }

        public decimal? FixQuantity(string sheet, ProductionScheduleImportTable table, int row,
            ProductionScheduleImportRowFix? fix, decimal? source)
        {
            if (fix?.Quantity is not { } quantity) return source;
            if (IsPositive(quantity)) Note($"{Source(sheet, table, row)}: cantidad {quantity.ToString(CultureInfo.InvariantCulture)}");
            return quantity;
        }

        private static Dictionary<string, (string Text, T Value)> Links<T>(IEnumerable<KeyValuePair<string, T>> links,
            Func<string, string> normalize) => links
            .Select(x => (Key: normalize(x.Key), Text: x.Key.Trim(), x.Value))
            .Where(x => x.Key.Length > 0).GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => (x.Last().Text, x.Last().Value), StringComparer.Ordinal);

        private static string Source(string sheet, ProductionScheduleImportTable table, int row) => $"{sheet} · {table switch
        {
            ProductionScheduleImportTable.Plan => "programa",
            ProductionScheduleImportTable.Execution => "ejecución",
            _ => "arrastre"
        }} fila {row}";

        private void Note(string value)
        {
            if (!applied.Contains(value, StringComparer.Ordinal)) applied.Add(value);
        }
    }
}
