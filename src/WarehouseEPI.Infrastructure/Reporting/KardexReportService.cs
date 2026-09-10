using System.Data;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Reporting;

public sealed record KardexFilter(Guid ProductId, Guid? LocationId = null, DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null, InventoryMovementType? Type = null, bool IncludeCorrections = true,
    int PageNumber = 1, int PageSize = 25, string TimeZoneId = "UTC", bool IncludeCorrectionDetails = false);

public sealed record KardexSummary(decimal InitialBalance, decimal TotalEntries, decimal TotalExits,
    decimal TotalTransfers, decimal NetAdjustments, decimal EndingBalance, decimal CurrentPhysicalBalance);

public sealed record KardexCorrectionDetail(Guid CorrectionId, string Relation, string Reason,
    DateTimeOffset RecordedAt, DateTimeOffset RecordedAtLocal, string RequestedBy, string AuthorizedBy,
    Guid OriginalMovementId, Guid ReversalMovementId, Guid? ReplacementMovementId,
    string OriginalDetailsUrl, string ReversalDetailsUrl, string? ReplacementDetailsUrl);

public sealed record KardexRow(Guid MovementId, Guid MovementLineId, DateTimeOffset OccurredAt,
    DateTimeOffset OccurredAtLocal, InventoryMovementType Type, InventoryMovementPurpose Purpose,
    string PurposeLabel, string? Reference, string? SourceLocationCode, string? DestinationLocationCode,
    string? RouteOrLocation, string? LotNumber, DateOnly? LotDate, string Status, string ResponsibleName,
    decimal Quantity, decimal EntryQuantity, decimal ExitQuantity, decimal NetDelta, decimal RunningBalance,
    bool IsNegative, bool PassedToNegative, IReadOnlyList<KardexCorrectionDetail> Corrections, string DetailsUrl);

public sealed record KardexResult(Product Product, Location? ScopedLocation, KardexSummary Summary,
    IReadOnlyList<KardexRow> Rows, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc,
    DateTimeOffset? FromLocal, DateTimeOffset? ToLocal, int PageNumber, int PageSize,
    int TotalLines, decimal BalanceBeforePage)
{
    public int TotalPages => TotalLines == 0 || PageSize == int.MaxValue ? 1 : (int)Math.Ceiling(TotalLines / (double)PageSize);
    public bool IsPaged => PageSize < int.MaxValue;
}

public sealed class KardexExportLimitExceededException(int limit)
    : InvalidOperationException($"El Kardex supera el límite de {limit:N0} filas. Reduce el período o selecciona una ubicación.")
{
    public int Limit { get; } = limit;
}

public sealed class KardexReportService(WarehouseDbContext dbContext)
{
    public Task<KardexResult?> GetKardexAsync(KardexFilter filter, CancellationToken token = default) =>
        BuildAsync(filter with { PageNumber = 1, PageSize = int.MaxValue }, null, token);

    public Task<KardexResult?> GetKardexPageAsync(KardexFilter filter, CancellationToken token = default) =>
        BuildAsync(filter with { PageSize = Math.Clamp(filter.PageSize, 1, 100) }, null, token);

    public Task<KardexResult?> GetKardexExportAsync(KardexFilter filter, int maximumRows = 10_000,
        CancellationToken token = default) => BuildAsync(
            filter with { PageNumber = 1, PageSize = int.MaxValue, IncludeCorrectionDetails = true }, maximumRows, token);

    private async Task<KardexResult?> BuildAsync(KardexFilter filter, int? maximumRows, CancellationToken token)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token) : null;

        var product = await dbContext.Products.AsNoTracking().Include(p => p.BaseUnit)
            .FirstOrDefaultAsync(p => p.Id == filter.ProductId, token);
        if (product is null) return null;

        Location? location = null;
        if (filter.LocationId.HasValue)
        {
            location = await dbContext.Locations.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == filter.LocationId.Value, token);
            if (location is null) return null;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(filter.TimeZoneId);
        var balanceQuery = dbContext.InventoryBalances.AsNoTracking().Where(b => b.ProductId == filter.ProductId);
        if (filter.LocationId.HasValue) balanceQuery = balanceQuery.Where(b => b.LocationId == filter.LocationId.Value);
        var currentBalance = await balanceQuery.SumAsync(b => (decimal?)b.Quantity, token) ?? 0m;

        var futureChanges = dbContext.InventoryBalanceChanges.AsNoTracking()
            .Where(c => c.MovementLine.ProductId == filter.ProductId);
        if (filter.FromUtc.HasValue) futureChanges = futureChanges.Where(c => c.MovementLine.Movement.OccurredAt >= filter.FromUtc.Value);
        if (filter.LocationId.HasValue) futureChanges = futureChanges.Where(c => c.LocationId == filter.LocationId.Value);
        var initialBalance = currentBalance - (await futureChanges.SumAsync(c => (decimal?)c.DeltaQuantity, token) ?? 0m);

        var filteredLines = ApplyLineFilter(filter);
        var totalLines = await filteredLines.CountAsync(token);
        if (maximumRows.HasValue && totalLines > maximumRows.Value)
            throw new KardexExportLimitExceededException(maximumRows.Value);

        var effects = filteredLines.Select(line => new
        {
            Type = line.Movement.Type,
            line.Quantity,
            Delta = !filter.LocationId.HasValue && line.Movement.Type == InventoryMovementType.Transfer ? 0m
                : line.BalanceChanges.Where(c => !filter.LocationId.HasValue || c.LocationId == filter.LocationId.Value)
                    .Sum(c => (decimal?)c.DeltaQuantity) ?? 0m
        });
        var entries = await effects.Where(x => x.Delta > 0).SumAsync(x => (decimal?)x.Delta, token) ?? 0m;
        var exits = -(await effects.Where(x => x.Delta < 0).SumAsync(x => (decimal?)x.Delta, token) ?? 0m);
        var transfers = await effects.Where(x => x.Type == InventoryMovementType.Transfer)
            .SumAsync(x => (decimal?)x.Quantity, token) ?? 0m;
        var adjustments = await effects.Where(x => x.Type == InventoryMovementType.Adjustment)
            .SumAsync(x => (decimal?)x.Delta, token) ?? 0m;

        var pageSize = filter.PageSize <= 0 ? 25 : filter.PageSize;
        var totalPages = totalLines == 0 || pageSize == int.MaxValue ? 1 : (int)Math.Ceiling(totalLines / (double)pageSize);
        var pageNumber = Math.Clamp(filter.PageNumber, 1, totalPages);
        var skip = pageSize == int.MaxValue ? 0 : (pageNumber - 1) * pageSize;
        var priorDelta = skip == 0 ? 0m : await OrderLines(filteredLines).Take(skip)
            .Select(line => !filter.LocationId.HasValue && line.Movement.Type == InventoryMovementType.Transfer ? 0m
                : line.BalanceChanges.Where(c => !filter.LocationId.HasValue || c.LocationId == filter.LocationId.Value)
                    .Sum(c => (decimal?)c.DeltaQuantity) ?? 0m).SumAsync(token);

        var take = pageSize == int.MaxValue ? (maximumRows ?? int.MaxValue) : pageSize;
        var lines = await OrderLines(filteredLines).Skip(skip).Take(take)
            .Include(l => l.Movement).ThenInclude(m => m.ResponsibleUser)
            .Include(l => l.Movement).ThenInclude(m => m.OperationalArea)
            .Include(l => l.SourceLocation).Include(l => l.DestinationLocation).Include(l => l.Lot)
            .Include(l => l.BalanceChanges).ThenInclude(c => c.Location).AsSplitQuery().ToListAsync(token);

        var movementIds = lines.Select(l => l.MovementId).Distinct().ToArray();
        var corrections = await GetCorrectionsAsync(movementIds, filter.IncludeCorrectionDetails, timeZone, token);
        var rows = BuildRows(lines, filter, location, initialBalance + priorDelta, timeZone, corrections, maximumRows);
        var summary = new KardexSummary(initialBalance, entries, exits, transfers, adjustments,
            initialBalance + entries - exits, currentBalance);
        var result = new KardexResult(product, location, summary, rows, filter.FromUtc, filter.ToUtc,
            ToLocal(filter.FromUtc, timeZone), ToLocal(filter.ToUtc, timeZone), pageNumber, pageSize,
            totalLines, initialBalance + priorDelta);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    private IQueryable<InventoryMovementLine> ApplyLineFilter(KardexFilter filter)
    {
        var query = dbContext.InventoryMovementLines.AsNoTracking().Where(l => l.ProductId == filter.ProductId);
        if (filter.FromUtc.HasValue) query = query.Where(l => l.Movement.OccurredAt >= filter.FromUtc.Value);
        if (filter.ToUtc.HasValue) query = query.Where(l => l.Movement.OccurredAt < filter.ToUtc.Value);
        if (filter.Type.HasValue) query = query.Where(l => l.Movement.Type == filter.Type.Value);
        if (filter.LocationId.HasValue) query = query.Where(l => l.BalanceChanges.Any(c => c.LocationId == filter.LocationId.Value));
        return query;
    }

    private static IOrderedQueryable<InventoryMovementLine> OrderLines(IQueryable<InventoryMovementLine> query) =>
        query.OrderBy(l => l.Movement.OccurredAt).ThenBy(l => l.Movement.RecordedAt)
            .ThenBy(l => l.MovementId).ThenBy(l => l.LineNumber).ThenBy(l => l.Id);

    private static List<KardexRow> BuildRows(IReadOnlyList<InventoryMovementLine> lines, KardexFilter filter,
        Location? location, decimal balanceBeforePage, TimeZoneInfo timeZone,
        IReadOnlyDictionary<Guid, IReadOnlyList<KardexCorrectionDetail>> corrections, int? maximumRows)
    {
        var running = balanceBeforePage;
        var rows = new List<KardexRow>();
        foreach (var line in lines)
        {
            var movement = line.Movement;
            var transfer = movement.Type == InventoryMovementType.Transfer;
            var global = !filter.LocationId.HasValue;
            var changes = filter.LocationId is Guid locationId
                ? line.BalanceChanges.Where(c => c.LocationId == locationId) : line.BalanceChanges.AsEnumerable();
            var lots = changes.GroupBy(c => new { c.LotId, c.LotNumberSnapshot, c.LotDateSnapshot })
                .Select(g => new { g.Key.LotId, LotNumber = g.Key.LotNumberSnapshot, LotDate = g.Key.LotDateSnapshot,
                    Delta = global && transfer ? 0m : g.Sum(c => c.DeltaQuantity),
                    Quantity = transfer && global ? g.Where(c => c.DeltaQuantity < 0).Sum(c => -c.DeltaQuantity)
                        : Math.Abs(g.Sum(c => c.DeltaQuantity)) })
                .OrderBy(g => g.LotDate).ThenBy(g => g.LotNumber, StringComparer.Ordinal).ThenBy(g => g.LotId).ToArray();
            if (lots.Length == 0)
                lots = [new { LotId = line.LotId, LotNumber = line.Lot?.Number, LotDate = line.Lot?.LotDate, Delta = 0m, Quantity = line.Quantity }];

            var route = transfer ? $"{line.SourceLocation?.Code ?? "—"} → {line.DestinationLocation?.Code ?? "—"}"
                : global ? line.DestinationLocation?.Code ?? line.SourceLocation?.Code ?? movement.OperationalArea?.Code ?? "—" : location!.Code;
            var related = corrections.GetValueOrDefault(movement.Id, []);
            var status = related.Count == 0 ? "Vigente" : string.Join(" · ", related.Select(c => c.Relation).Distinct());
            foreach (var lot in lots)
            {
                if (maximumRows.HasValue && rows.Count >= maximumRows.Value)
                    throw new KardexExportLimitExceededException(maximumRows.Value);
                var previous = running;
                running += lot.Delta;
                rows.Add(new KardexRow(movement.Id, line.Id, movement.OccurredAt,
                    TimeZoneInfo.ConvertTime(movement.OccurredAt, timeZone), movement.Type, movement.Purpose,
                    PurposeLabel(movement.Purpose), movement.Reference, line.SourceLocation?.Code,
                    line.DestinationLocation?.Code, route, lot.LotNumber ?? line.Lot?.Number,
                    lot.LotDate ?? line.Lot?.LotDate, status, movement.ResponsibleUser.FullName,
                    lot.Quantity, lot.Delta > 0 ? lot.Delta : 0m, lot.Delta < 0 ? -lot.Delta : 0m,
                    lot.Delta, running, running < 0, previous >= 0 && running < 0, related,
                    $"/Admin/Inventory/Movements/Details/{movement.Id}"));
            }
        }
        return rows;
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<KardexCorrectionDetail>>> GetCorrectionsAsync(
        Guid[] movementIds, bool includeDetails, TimeZoneInfo timeZone, CancellationToken token)
    {
        if (movementIds.Length == 0) return new Dictionary<Guid, IReadOnlyList<KardexCorrectionDetail>>();
        var records = await dbContext.InventoryMovementCorrections.AsNoTracking()
            .Where(c => movementIds.Contains(c.OriginalMovementId) || movementIds.Contains(c.ReversalMovementId)
                || (c.ReplacementMovementId.HasValue && movementIds.Contains(c.ReplacementMovementId.Value)))
            .Select(c => new { c.Id, c.OriginalMovementId, c.ReversalMovementId, c.ReplacementMovementId,
                Reason = includeDetails ? c.Reason : string.Empty, c.RecordedAt,
                RequestedBy = includeDetails ? c.RequestedByUser.FullName : string.Empty,
                AuthorizedBy = includeDetails ? c.AuthorizedByUser.FullName : string.Empty }).ToListAsync(token);
        var result = new Dictionary<Guid, List<KardexCorrectionDetail>>();
        foreach (var record in records)
        {
            Add(record.OriginalMovementId, "Original corregido");
            Add(record.ReversalMovementId, "Reverso");
            if (record.ReplacementMovementId.HasValue) Add(record.ReplacementMovementId.Value, "Reemplazo");
            void Add(Guid movementId, string relation)
            {
                if (!result.TryGetValue(movementId, out var list)) result[movementId] = list = [];
                list.Add(new(record.Id, relation, record.Reason, record.RecordedAt,
                    TimeZoneInfo.ConvertTime(record.RecordedAt, timeZone), record.RequestedBy, record.AuthorizedBy,
                    record.OriginalMovementId, record.ReversalMovementId, record.ReplacementMovementId,
                    $"/Admin/Inventory/Movements/Details/{record.OriginalMovementId}",
                    $"/Admin/Inventory/Movements/Details/{record.ReversalMovementId}",
                    record.ReplacementMovementId.HasValue ? $"/Admin/Inventory/Movements/Details/{record.ReplacementMovementId.Value}" : null));
            }
        }
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<KardexCorrectionDetail>)p.Value.OrderBy(x => x.RecordedAt).ToArray());
    }

    private static DateTimeOffset? ToLocal(DateTimeOffset? value, TimeZoneInfo zone) =>
        value.HasValue ? TimeZoneInfo.ConvertTime(value.Value, zone) : null;

    private static string PurposeLabel(InventoryMovementPurpose purpose) => purpose switch
    {
        InventoryMovementPurpose.Standard => "Operación estándar",
        InventoryMovementPurpose.GeneralExit => "Salida general",
        InventoryMovementPurpose.ProductionIssue => "Salida a producción",
        InventoryMovementPurpose.WipWarehouseReturn => "Regreso desde WIP",
        InventoryMovementPurpose.WipConsumption => "Consumo WIP",
        InventoryMovementPurpose.WipSupplierReturn => "Devolución WIP a proveedor",
        InventoryMovementPurpose.CycleCountAdjustment => "Ajuste por conteo",
        InventoryMovementPurpose.DocumentReceipt => "Recepción de documento",
        _ => purpose.ToString()
    };
}
