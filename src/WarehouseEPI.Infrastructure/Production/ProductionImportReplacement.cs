using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Production;

public sealed record ProductionImportReplacementImpact(DateOnly WeekStart, Guid? WeekId,
    int ReplacedLines, int IncomingLines, int PreservedCaptures)
{
    public IReadOnlyList<ProductionImportOffProgramLine> OffProgramLines { get; init; } = [];
}
public sealed record ProductionImportOffProgramLine(Guid LineId, int Sequence, string Sku);

public sealed partial class ProductionScheduleImportService
{
    internal const string DetachedLineAction = "programming-detached";
    internal static Task<List<Guid>> DetachedLineIdsAsync(WarehouseDbContext context, Guid[] ids, CancellationToken token) =>
        context.ProductionScheduleRevisions.AsNoTracking().Where(x => x.Action == DetachedLineAction &&
            x.LineId.HasValue && ids.Contains(x.LineId.Value)).Select(x => x.LineId!.Value).Distinct().ToListAsync(token);

    private async Task<IReadOnlyList<ProductionImportReplacementImpact>> ReplacementImpactAsync(
        IEnumerable<ProductionScheduleImportWeek> source, CancellationToken token)
    {
        var dates = source.Select(x => x.WeekStart).ToArray();
        var weeks = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => dates.Contains(x.WeekStart))
            .Select(x => new
            {
                x.Id,
                x.WeekStart,
                Lines = x.Lines.Count(l => !l.IsCancelled && !l.IsExtra && !l.IsCarryover),
                Captures = x.Captures.Count
            }).ToListAsync(token);
        var result = new List<ProductionImportReplacementImpact>();
        foreach (var incoming in source)
        {
            var prior = weeks.SingleOrDefault(w => w.WeekStart == incoming.WeekStart);
            var old = await db.ProductionScheduleLines.AsNoTracking().Include(x => x.Product)
                .Where(x => x.WeekId == (prior == null ? Guid.Empty : prior.Id) && !x.IsCancelled && !x.IsExtra && !x.IsCarryover).ToArrayAsync(token);
            var linked = await LinkedReplacementLinesAsync(old.Select(x => x.Id).ToArray(), token);
            var matched = MatchReplacementLines(old, incoming.FinalLines).Where(x => x.Existing != null).Select(x => x.Existing!.Id).ToHashSet();
            result.Add(new(incoming.WeekStart, prior?.Id, prior?.Lines ?? 0, incoming.FinalLines.Count, prior?.Captures ?? 0)
            {
                OffProgramLines = old.Where(x => linked.Contains(x.Id) && !matched.Contains(x.Id))
                    .OrderBy(x => x.Sequence).Select(x => new ProductionImportOffProgramLine(x.Id, x.Sequence, x.Product.Sku)).ToArray()
            });
        }
        return result;
    }

    private async Task<IReadOnlyList<ProductionScheduleImportIssue>> ReplacementIssuesAsync(
        IEnumerable<ProductionScheduleImportWeek> source, CancellationToken token)
    {
        var errors = new List<ProductionScheduleImportIssue>();
        foreach (var incoming in source)
        {
            var week = await db.ProductionScheduleWeeks.AsNoTracking().Include(x => x.Lines).ThenInclude(x => x.Product)
                .Include(x => x.Lines).ThenInclude(x => x.WorkOrder)
                .SingleOrDefaultAsync(x => x.WeekStart == incoming.WeekStart, token);
            if (week is null) continue;
            var old = week.Lines.Where(x => !x.IsCancelled && !x.IsExtra && !x.IsCarryover).ToArray();
            var linked = await LinkedReplacementLinesAsync(old.Select(x => x.Id).ToArray(), token);
            var matches = MatchReplacementLines(old, incoming.FinalLines);
            foreach (var line in old.Where(x => linked.Contains(x.Id)))
            {
                var match = matches.SingleOrDefault(x => x.Existing?.Id == line.Id);
                void Issue(string message) => errors.Add(new(incoming.Sheet, match?.Source.SourceRow,
                    $"{line.Product.Sku} · renglón {line.Sequence}: {message}"));
                if (match is null)
                {
                    if (await db.ProductionWeekOpenings.AnyAsync(x => x.SourceLineId == line.Id && x.Quantity > 0, token))
                        Issue("El archivo elimina una línea comprometida en arrastres posteriores. Conserva ese renglón o ajusta primero sus arrastres; las capturas no se eliminan.");
                    continue;
                }
                if (SameReplacementLine(line, match.Source)) continue;
                var produced = await db.ProductionDailyCaptureAllocations.AsNoTracking()
                    .Where(x => x.ScheduleLineId == line.Id && x.Capture.Status == ProductionDailyCaptureStatus.Active)
                    .GroupBy(x => x.WorkOrderStageId).Select(x => x.Sum(a => a.Quantity)).ToListAsync(token);
                var minimum = produced.DefaultIfEmpty().Max();
                if (match.Source.Quantity < minimum)
                    Issue($"La cantidad {match.Source.Quantity:0.####} es menor que las {minimum:0.####} ya producidas. Conserva esa cantidad o corrige primero la captura.");
                var commitments = await db.ProductionWeekOpenings.AsNoTracking()
                    .Where(x => x.SourceLineId == line.Id && x.SourceWeekId == week.Id && x.Quantity > 0)
                    .GroupBy(x => x.Area).Select(x => new { Area = x.Key, Quantity = x.Sum(o => o.Quantity) }).ToListAsync(token);
                foreach (var commitment in commitments)
                {
                    var consumed = await db.ProductionDailyCaptureAllocations.AsNoTracking().Where(x => x.ScheduleLineId == line.Id &&
                        x.Capture.WeekId == week.Id && x.Capture.Area == commitment.Area && x.Capture.Status == ProductionDailyCaptureStatus.Active)
                        .SumAsync(x => x.Quantity, token);
                    if (match.Source.Quantity < consumed + commitment.Quantity)
                        Issue($"La cantidad {match.Source.Quantity:0.####} no cubre {consumed:0.####} producidas y {commitment.Quantity:0.####} comprometidas en arrastres ({commitment.Area}).");
                }
                if (line.WorkOrderId.HasValue && (line.WorkOrder is null ||
                    !ProductionActionPolicy.Allows(line.WorkOrder.Status, "adjust") &&
                    (line.WorkOrder.TargetQuantity != match.Source.Quantity || line.WorkOrder.AuthorizedQuantity != match.Source.Quantity || line.WorkOrder.DueDate != match.Source.Date)))
                    Issue("La orden vinculada no admite ajustes. Reabre la orden antes de cambiar su programación.");
            }
        }
        return errors;
    }

    private sealed record ReplacementMatch(ProductionScheduleImportLine Source, ProductionScheduleLine? Existing);

    // Match all exact identities first, then remaining occurrences of the same SKU in stable order.
    // Never relink recorded production to another product.
    private static IReadOnlyList<ReplacementMatch> MatchReplacementLines(IReadOnlyList<ProductionScheduleLine> old,
        IReadOnlyList<ProductionScheduleImportLine> incoming)
    {
        var rows = incoming.Where(x => !x.IsCarryover).OrderBy(x => x.Date).ThenBy(x => x.SourceRow).ToArray();
        var found = new Dictionary<int, ProductionScheduleLine>();
        var available = old.OrderBy(x => x.PlannedDate).ThenBy(x => x.Sequence).ThenBy(x => x.Id).ToList();
        for (var pass = 0; pass < 5; pass++)
            for (var i = 0; i < rows.Length; i++)
            {
                if (found.ContainsKey(i)) continue;
                var row = rows[i];
                var match = available.FirstOrDefault(x => x.ProductId == row.ProductId && (pass switch
                {
                    0 => x.PlannedDate == row.Date && SameReferences(x, row) && x.Quantity == row.Quantity,
                    1 => x.PlannedDate == row.Date && SameReferences(x, row),
                    2 => SameReferences(x, row),
                    3 => x.PlannedDate == row.Date,
                    _ => true
                }));
                if (match is not null) { found[i] = match; available.Remove(match); }
            }
        return rows.Select((row, i) => new ReplacementMatch(row, found.GetValueOrDefault(i))).ToArray();
    }

    private static bool SameReferences(ProductionScheduleLine x, ProductionScheduleImportLine y) =>
        x.OrderReference1 == y.Reference1 && x.OrderReference2 == y.Reference2 && x.OrderReference3 == y.Reference3;
    private static bool SameReplacementLine(ProductionScheduleLine x, ProductionScheduleImportLine y) =>
        x.ProductId == y.ProductId && x.PlannedDate == y.Date && x.Quantity == y.Quantity && SameReferences(x, y) &&
        x.Notes == y.Notes && x.OriginalType == y.OriginalType && x.OriginalAnnotation1 == y.OriginalAnnotation1 &&
        x.OriginalAnnotation2 == y.OriginalAnnotation2 && x.OriginalAnnotation1Kind == y.OriginalAnnotation1Kind &&
        x.OriginalAnnotation2Kind == y.OriginalAnnotation2Kind;

    private async Task<HashSet<Guid>> LinkedReplacementLinesAsync(Guid[] ids, CancellationToken token) =>
        (await db.ProductionScheduleLines.AsNoTracking().Where(x => ids.Contains(x.Id) && (x.WorkOrderId != null ||
            x.CaptureAllocations.Any() || db.ProductionWeekOpenings.Any(o => o.SourceLineId == x.Id && o.Quantity > 0)))
            .Select(x => x.Id).ToListAsync(token)).ToHashSet();

    private async Task<string> ReplacementHashAsync(IEnumerable<ProductionScheduleImportWeek> source, CancellationToken token)
    {
        var dates = source.Select(x => x.WeekStart).ToArray();
        var weeks = await db.ProductionScheduleWeeks.AsNoTracking().Where(x => dates.Contains(x.WeekStart))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.Status, x.ExplicitCarryover }).ToListAsync(token);
        var ids = weeks.Select(x => x.Id).ToArray();
        var lines = await db.ProductionScheduleLines.AsNoTracking().Where(x => ids.Contains(x.WeekId))
            .OrderBy(x => x.Id).Select(x => new
            {
                x.Id,
                x.Version,
                x.ProductId,
                x.Quantity,
                x.PlannedDate,
                x.IsCancelled,
                x.IsCarryover,
                x.IsExtra,
                x.WorkOrderId,
                x.Sequence,
                x.OrderReference1,
                x.OrderReference2,
                x.OrderReference3,
                x.Notes
            }).ToListAsync(token);
        var captures = await db.ProductionDailyCaptures.AsNoTracking().Where(x => ids.Contains(x.WeekId))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Status, x.Quantity }).ToListAsync(token);
        var lineIds = lines.Select(x => x.Id).ToArray();
        var openings = await db.ProductionWeekOpenings.AsNoTracking().Where(x => ids.Contains(x.SourceWeekId) || ids.Contains(x.WeekId) || lineIds.Contains(x.SourceLineId))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.SourceLineId, x.Quantity, x.SourceFingerprint }).ToListAsync(token);
        var orderIds = lines.Where(x => x.WorkOrderId.HasValue).Select(x => x.WorkOrderId!.Value).ToArray();
        var orders = await db.ProductionWorkOrders.AsNoTracking().Where(x => orderIds.Contains(x.Id)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Version, x.Status, x.TargetQuantity, x.AuthorizedQuantity }).ToListAsync(token);
        var allocations = await db.ProductionDailyCaptureAllocations.AsNoTracking().Where(x => lineIds.Contains(x.ScheduleLineId))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.ScheduleLineId, x.Quantity, x.Capture.Status, x.Capture.Area, x.Capture.WeekId }).ToListAsync(token);
        return CanonicalHash(new { Algorithm = "linked-replacement-v3", Catalog = await GetDependencyHashAsync(source, token), weeks, lines, captures, openings, orders, allocations });
    }

    private async Task<ProductionDailyCommandResult> ReplaceProgrammingAsync(ProductionScheduleImportPreview preview,
        Guid operationId, Guid actorId, CancellationToken token)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        try
        {
            var prior = await db.ProductionScheduleRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operationId, token);
            if (prior is not null)
                return prior.RequestFingerprint == preview.Fingerprint
                    ? new(ProductionDailyCommandStatus.Success, prior.WeekId) : new(ProductionDailyCommandStatus.IdempotencyConflict);
            if (await db.ProductionScheduleImportBatches.AnyAsync(x => x.OperationId == operationId, token))
                return new(ProductionDailyCommandStatus.IdempotencyConflict);
            if (preview.DependencyHash != await ReplacementHashAsync(preview.Weeks, token))
                return new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: ["La revisión cambió. Vuelve a validar antes de confirmar."]);
            var issues = await ReplacementIssuesAsync(preview.Weeks, token);
            if (issues.Count > 0) return new(ProductionDailyCommandStatus.ValidationFailed, Errors: issues.Select(x => x.Message).ToArray());
            var dates = preview.Weeks.Select(x => x.WeekStart).ToArray();
            var changingWeeks = await db.ProductionScheduleWeeks.Where(x => dates.Contains(x.WeekStart)).Select(x => x.Id).ToArrayAsync(token);
            var changingLines = await db.ProductionScheduleLines.Where(x => changingWeeks.Contains(x.WeekId)).Select(x => x.Id).ToArrayAsync(token);
            var affectedOpenings = await db.ProductionWeekOpenings.Where(x => x.Quantity > 0 &&
                (changingWeeks.Contains(x.SourceWeekId) || changingLines.Contains(x.SourceLineId))).ToListAsync(token);
            var openingService = new ProductionWeekOpeningService(db);
            var reviewedOpenings = new List<ProductionWeekOpening>();
            foreach (var group in affectedOpenings.GroupBy(x => x.WeekId))
            {
                var options = await openingService.OptionsAsync(group.Key, token);
                reviewedOpenings.AddRange(group.Where(row => options.Any(x => x.SourceWeekId == row.SourceWeekId &&
                    x.SourceLineId == row.SourceLineId && x.Area == row.Area && x.Available >= row.Quantity && x.Fingerprint == row.SourceFingerprint)));
            }
            Guid? firstWeek = null;
            foreach (var source in preview.Weeks)
            {
                var week = await db.ProductionScheduleWeeks.Include(x => x.Lines).SingleOrDefaultAsync(x => x.WeekStart == source.WeekStart, token);
                if (week is null)
                {
                    week = new ProductionScheduleWeek
                    {
                        OperationId = Derive(operationId, source.WeekStart, "week"),
                        RequestFingerprint = preview.Fingerprint,
                        WeekStart = source.WeekStart,
                        WeekEnd = source.WeekStart.AddDays(ProductionWeekCalendar.LastDayOffset),
                        ExplicitCarryover = true,
                        Origin = ProductionScheduleOrigin.ExcelImport,
                        SourceName = source.Sheet,
                        CreatedByUserId = actorId,
                        CreatedAt = timeProvider.GetUtcNow()
                    };
                    db.ProductionScheduleWeeks.Add(week);
                }
                var old = week.Lines.Where(x => !x.IsCancelled && !x.IsExtra && !x.IsCarryover).ToArray();
                var before = JsonSerializer.Serialize(old.Select(ReplacementLineSnapshot));
                var linked = await LinkedReplacementLinesAsync(old.Select(x => x.Id).ToArray(), token);
                var matches = MatchReplacementLines(old, source.FinalLines);
                foreach (var line in old)
                {
                    if (linked.Contains(line.Id))
                    {
                        if (matches.Any(x => x.Existing?.Id == line.Id)) continue;
                        var previous = JsonSerializer.Serialize(ReplacementLineSnapshot(line));
                        // Remove only the plan contribution. Keep the order and every capture/allocation unchanged.
                        line.IsExtra = true;
                        line.Version++;
                        db.ProductionScheduleRevisions.Add(new ProductionScheduleRevision
                        {
                            OperationId = Derive(operationId, source.WeekStart, $"detach:{line.Id}"),
                            RequestFingerprint = preview.Fingerprint,
                            WeekId = week.Id,
                            LineId = line.Id,
                            Action = DetachedLineAction,
                            BeforeJson = previous,
                            AfterJson = JsonSerializer.Serialize(ReplacementLineSnapshot(line)),
                            ResponsibleUserId = actorId,
                            RecordedAt = timeProvider.GetUtcNow()
                        });
                    }
                    else { line.IsCancelled = true; line.Version++; }
                }
                var sequence = week.Lines.Select(x => x.Sequence).DefaultIfEmpty().Max();
                var added = new List<ProductionScheduleLine>();
                foreach (var match in matches)
                {
                    var line = match.Source;
                    var retained = match.Existing is { } existing && linked.Contains(existing.Id) ? existing : null;
                    if (retained is not null || week.Status == ProductionScheduleWeekStatus.Open)
                    {
                        if (retained is not null && SameReplacementLine(retained, line)) { added.Add(retained); continue; }
                        if (scheduleService is null) return await AbortReplacementAsync(new(ProductionDailyCommandStatus.ValidationFailed,
                            Errors: ["El servicio de programación no está disponible."]));
                        var saved = await scheduleService.SaveImportedLineAsync(new(
                            Derive(operationId, source.WeekStart, $"save:{line.SourceRow}"), week.Id, retained?.Id,
                            week.Version, retained?.Version, line.Date, line.ProductId, line.Quantity,
                            line.Reference1, line.Reference2, line.Reference3, line.Notes, actorId, "",
                            line.OriginalType, line.OriginalAnnotation1, line.OriginalAnnotation2,
                            line.OriginalAnnotation1Kind, line.OriginalAnnotation2Kind), token);
                        if (!saved.Success) return await AbortReplacementAsync(saved with
                        {
                            Errors = (saved.Errors ?? ["No fue posible ajustar la orden vinculada."])
                                .Select(x => $"{source.Sheet} · {line.Sku} · fila {line.SourceRow}: {x}").ToArray()
                        });
                        var updated = await db.ProductionScheduleLines.SingleAsync(x => x.Id == saved.Id, token);
                        updated.SourceSheet = line.SourceSheet ?? source.Sheet;
                        updated.SourceRow = line.SourceRow;
                        added.Add(updated);
                        continue;
                    }
                    var replacement = new ProductionScheduleLine
                    {
                        WeekId = week.Id,
                        Sequence = ++sequence,
                        PlannedDate = line.Date,
                        ProductId = line.ProductId,
                        Quantity = line.Quantity,
                        OrderReference1 = line.Reference1,
                        OrderReference2 = line.Reference2,
                        OrderReference3 = line.Reference3,
                        Notes = line.Notes,
                        Origin = ProductionScheduleOrigin.ExcelImport,
                        StartArea = line.StartArea,
                        SourceSheet = line.SourceSheet ?? source.Sheet,
                        SourceRow = line.SourceRow,
                        OriginalType = line.OriginalType,
                        OriginalAnnotation1 = line.OriginalAnnotation1,
                        OriginalAnnotation2 = line.OriginalAnnotation2,
                        OriginalAnnotation1Kind = line.OriginalAnnotation1Kind,
                        OriginalAnnotation2Kind = line.OriginalAnnotation2Kind
                    };
                    week.Lines.Add(replacement);
                    db.ProductionScheduleLines.Add(replacement);
                    added.Add(replacement);
                }
                week.Version++;
                db.ProductionScheduleRevisions.Add(new ProductionScheduleRevision
                {
                    OperationId = firstWeek is null ? operationId : Derive(operationId, source.WeekStart, "replace-programming"),
                    RequestFingerprint = preview.Fingerprint,
                    WeekId = week.Id,
                    Action = "programming-replaced",
                    BeforeJson = before,
                    AfterJson = JsonSerializer.Serialize(new
                    {
                        preview.FileName,
                        preview.FileHash,
                        Lines = added.Select(ReplacementLineSnapshot)
                    }),
                    ResponsibleUserId = actorId,
                    RecordedAt = timeProvider.GetUtcNow()
                });
                firstWeek ??= week.Id;
            }
            await db.SaveChangesAsync(token);
            foreach (var group in reviewedOpenings.GroupBy(x => x.WeekId))
            {
                var options = await openingService.OptionsAsync(group.Key, token);
                var before = JsonSerializer.Serialize(group.Select(x => new { x.Id, x.SourceLineId, x.Quantity, x.SourceFingerprint, x.Version }));
                foreach (var row in group)
                {
                    var option = options.SingleOrDefault(x => x.SourceWeekId == row.SourceWeekId && x.SourceLineId == row.SourceLineId && x.Area == row.Area);
                    if (option is null || option.Available < row.Quantity)
                        return await AbortReplacementAsync(new(ProductionDailyCommandStatus.ValidationFailed,
                            Errors: [$"El reemplazo no cubre el arrastre de {row.Quantity:0.####} de la línea {row.SourceLineId}. Revisa la programación y el arrastre."]));
                    row.SourceFingerprint = option.Fingerprint;
                    row.Version++;
                }
                var targetDate = await db.ProductionScheduleWeeks.Where(x => x.Id == group.Key).Select(x => x.WeekStart).SingleAsync(token);
                db.ProductionScheduleRevisions.Add(new ProductionScheduleRevision
                {
                    OperationId = Derive(operationId, targetDate, "import-openings-reviewed"),
                    RequestFingerprint = preview.Fingerprint,
                    WeekId = group.Key,
                    Action = "import-openings-reviewed",
                    BeforeJson = before,
                    AfterJson = JsonSerializer.Serialize(group.Select(x => new { x.Id, x.SourceLineId, x.Quantity, x.SourceFingerprint, x.Version })),
                    ResponsibleUserId = actorId,
                    RecordedAt = timeProvider.GetUtcNow()
                });
            }
            if (reviewedOpenings.Count > 0) await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProductionDailyCommandStatus.Success, firstWeek);
        }
        catch (Exception ex) when (ex is DbUpdateException || ex is PostgresException { SqlState: "40001" or "40P01" })
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return new(ProductionDailyCommandStatus.ConcurrencyConflict, Errors: ["La revisión cambió. Vuelve a validar antes de confirmar."]);
        }

        async Task<ProductionDailyCommandResult> AbortReplacementAsync(ProductionDailyCommandResult result)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            return result;
        }
    }

    private static object ReplacementLineSnapshot(ProductionScheduleLine x) => new
    {
        x.Id,
        x.Sequence,
        x.PlannedDate,
        x.ProductId,
        x.Quantity,
        x.OrderReference1,
        x.OrderReference2,
        x.OrderReference3,
        x.Notes,
        x.Origin,
        x.OriginalType,
        x.OriginalAnnotation1,
        x.OriginalAnnotation2,
        x.OriginalAnnotation1Kind,
        x.OriginalAnnotation2Kind,
        x.StartArea,
        x.SourceSheet,
        x.SourceRow,
        x.WorkOrderId,
        x.IsExtra,
        x.IsCancelled,
        x.Version
    };
}
