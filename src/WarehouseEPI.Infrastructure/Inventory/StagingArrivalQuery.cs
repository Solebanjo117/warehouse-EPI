using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record StagingArrivalRow(Guid LineId, Guid MovementId, Guid LocationId, Guid ProductId,
    string Sku, string Description, string Unit, string Origin, string? Reference, DateTimeOffset OccurredAt,
    decimal Received, decimal? Pending, string Version, bool NeedsIdentification);
public sealed record StagingArrivalPage(IReadOnlyList<StagingArrivalRow> Items, bool HasMore);
public enum StagingPriority { Normal, Soon, Urgent }
public sealed record StagingPendingSummary(int Normal, int Soon, int Urgent)
{
    public int Total => Normal + Soon + Urgent;
}
public sealed record StagingArrivalOverview(StagingArrivalPage Page, StagingPendingSummary Summary);
public static class StagingArrivalAge
{
    public static TimeSpan Elapsed(DateTimeOffset occurredAt, DateTimeOffset asOf) => asOf > occurredAt ? asOf - occurredAt : TimeSpan.Zero;
    public static StagingPriority Priority(DateTimeOffset occurredAt, DateTimeOffset asOf) => Elapsed(occurredAt, asOf).TotalHours switch
    {
        >= 48 => StagingPriority.Urgent,
        >= 24 => StagingPriority.Soon,
        _ => StagingPriority.Normal
    };
    public static StagingPriority? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "normal" => StagingPriority.Normal, "soon" => StagingPriority.Soon, "urgent" => StagingPriority.Urgent, _ => null
    };
}
public sealed record StagingPutawayCommand(Guid OperationId, Guid ArrivalLineId, Guid DestinationId,
    string Version, string Pin, IReadOnlyCollection<Guid>? ApprovedLocations = null);

/// <summary>Arrival identity follows ingress events, not the original receipt of a reusable plate.</summary>
public sealed class StagingArrivalQuery(WarehouseDbContext db)
{
    internal IQueryable<InventoryMovementLine> Arrivals => db.InventoryMovementLines.AsNoTracking()
        .Where(l => l.DestinationLocation != null && l.DestinationLocation.Code == "STAGING" &&
            l.DestinationLocation.Kind == LocationKind.Area && l.DestinationLocation.OperationalRole != LocationOperationalRole.Wip &&
            (l.Movement.Type == InventoryMovementType.Entry || l.Movement.Type == InventoryMovementType.Transfer) &&
            (l.Movement.Purpose == InventoryMovementPurpose.Standard || l.Movement.Purpose == InventoryMovementPurpose.DocumentReceipt ||
             l.Movement.Purpose == InventoryMovementPurpose.ProductionReceipt || l.Movement.Purpose == InventoryMovementPurpose.WipWarehouseReturn) &&
            !db.InventoryMovementCorrections.Any(c => c.OriginalMovementId == l.MovementId || c.ReversalMovementId == l.MovementId));

    public static string EncodeVersion(IEnumerable<PalletSelection> plates) =>
        JsonSerializer.Serialize(plates.OrderBy(p => p.PlateId));

    public static IReadOnlyList<PalletSelection>? DecodeVersion(string? version)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 200000) return null;
        try
        {
            var plates = JsonSerializer.Deserialize<List<PalletSelection>>(version);
            return plates is { Count: > 0 and <= 1000 } && plates.All(p => p is not null && p.PlateId != Guid.Empty &&
                p.Quantity > 0 && p.Quantity <= InventoryMovementRules.MaximumQuantity && p.ExpectedVersion > 0) &&
                plates.Select(p => p.PlateId).Distinct().Count() == plates.Count ? plates : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<StagingArrivalRow?> GetAsync(Guid lineId, CancellationToken token = default) =>
        (await RowsAsync(Arrivals.Where(l => l.Id == lineId), token)).SingleOrDefault();

    public Task<IReadOnlyList<StagingArrivalRow>> ForMovementAsync(Guid movementId, CancellationToken token = default) =>
        RowsAsync(Arrivals.Where(l => l.MovementId == movementId), token);

    public async Task<StagingArrivalOverview> OverviewAsync(string? search, StagingPriority? priority,
        int page, DateTimeOffset asOf, CancellationToken token = default)
    {
        var term = search?.Trim().ToUpperInvariant();
        var skip = (Math.Clamp(page, 1, 100000) - 1) * 25;
        var result = new List<StagingArrivalRow>();
        var counts = new int[3];
        // Age-only priorities are monotonic: oldest first also sorts urgent before soon before normal.
        for (var offset = 0; ; offset += 100)
        {
            var batch = await RowsAsync(Arrivals.OrderBy(l => l.Movement.OccurredAt).ThenBy(l => l.Id).Skip(offset).Take(100), token);
            foreach (var row in batch.Where(IsPending))
            {
                var level = StagingArrivalAge.Priority(row.OccurredAt, asOf);
                counts[(int)level]++;
                if (result.Count == 26 || (priority.HasValue && priority != level)) continue;
                if (!string.IsNullOrEmpty(term) && !row.Sku.ToUpperInvariant().Contains(term) &&
                    !row.Description.ToUpperInvariant().Contains(term) && !(row.Reference?.ToUpperInvariant().Contains(term) ?? false)) continue;
                if (skip > 0) { skip--; continue; }
                result.Add(row);
            }
            if (batch.Count < 100) break;
        }
        return new(new(result.Take(25).ToArray(), result.Count > 25), new(counts[0], counts[1], counts[2]));
    }

    public async Task<StagingArrivalPage> ListAsync(string? search, int page = 1, CancellationToken token = default)
    {
        var query = Arrivals;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(l => l.Product.Sku.ToUpper().Contains(term) || (l.Product.Description != null && l.Product.Description.ToUpper().Contains(term)) ||
                (l.Movement.Reference != null && l.Movement.Reference.ToUpper().Contains(term)));
        }
        var skip = (Math.Clamp(page, 1, 100000) - 1) * 25;
        var result = new List<StagingArrivalRow>();
        // Scan bounded batches: filtering exhausted arrivals must precede visible pagination.
        for (var offset = 0; result.Count < 26; offset += 100)
        {
            var batch = await RowsAsync(query.OrderBy(l => l.Movement.OccurredAt).ThenBy(l => l.Id).Skip(offset).Take(100), token);
            foreach (var row in batch.Where(IsPending))
            {
                if (skip > 0) { skip--; continue; }
                result.Add(row);
                if (result.Count == 26) break;
            }
            if (batch.Count < 100) break;
        }
        return new(result.Take(25).ToArray(), result.Count > 25);
    }

    public async Task<int> CountPendingAsync(CancellationToken token = default)
    {
        var count = 0;
        for (var offset = 0; ; offset += 100)
        {
            var batch = await RowsAsync(Arrivals.OrderBy(l => l.Movement.OccurredAt).ThenBy(l => l.Id)
                .Skip(offset).Take(100), token);
            count += batch.Count(IsPending);
            if (batch.Count < 100) return count;
        }
    }

    private static bool IsPending(StagingArrivalRow row) => row.NeedsIdentification || row.Pending > 0;

    private async Task<IReadOnlyList<StagingArrivalRow>> RowsAsync(IQueryable<InventoryMovementLine> query, CancellationToken token)
    {
        var lines = await query.Include(l => l.Movement).Include(l => l.Product).Include(l => l.Unit)
            .Include(l => l.SourceLocation).ToListAsync(token);
        if (lines.Count == 0) return [];
        var ids = lines.Select(l => l.Id).ToArray();
        var candidateIds = await db.PalletPlateEvents.Where(e => e.MovementLineId.HasValue && ids.Contains(e.MovementLineId.Value))
            .Select(e => e.PlateId).Distinct().ToArrayAsync(token);
        var plates = await db.PalletPlates.AsNoTracking().Where(p => candidateIds.Contains(p.Id)).ToListAsync(token);
        var events = await db.PalletPlateEvents.AsNoTracking().Where(e => candidateIds.Contains(e.PlateId))
            .OrderBy(e => e.PlateVersion).ToListAsync(token);
        var histories = events.ToLookup(e => e.PlateId);
        var result = new List<StagingArrivalRow>();
        foreach (var line in lines)
        {
            var selected = new List<PalletSelection>();
            var evidence = false;
            var ambiguous = false;
            var recovered = events.Any(e => e.MovementLineId == line.Id && e.Kind == "StagingEntryIdentification");
            decimal originallyIdentified = 0;
            foreach (var plate in plates)
            {
                var history = histories[plate.Id].ToArray();
                var reversed = history.Where(e => e.ReversesEventId.HasValue).Select(e => e.ReversesEventId!.Value).ToHashSet();
                var effective = history.Where(e => !reversed.Contains(e.Id) && e.ReversesEventId == null).ToArray();
                var arrivals = effective.Where(e => IsIngress(e, line.DestinationLocationId!.Value)).ToArray();
                if (!arrivals.Any(e => e.MovementLineId == line.Id)) continue;
                if (recovered && !arrivals.Any(e => e.MovementLineId == line.Id && e.Kind is "StagingEntryIdentification" or "StagingSplitChild")) continue;
                foreach (var ingress in arrivals.Where(e => e.MovementLineId == line.Id && e.Kind != "StagingSplitChild"))
                {
                    var before = JsonSerializer.Deserialize<PalletState>(ingress.Before)!;
                    var after = JsonSerializer.Deserialize<PalletState>(ingress.After)!;
                    originallyIdentified += after.Quantity - (before.LocationId == line.DestinationLocationId && !before.IsVoided ? before.Quantity : 0);
                }
                evidence = true;
                var latest = arrivals.Last();
                var ownIngress = arrivals.Last(e => e.MovementLineId == line.Id);
                if (effective.Any(e => e.PlateVersion >= ownIngress.PlateVersion && IsMixed(e, line.DestinationLocationId!.Value)))
                { ambiguous = true; continue; }
                if (latest.MovementLineId != line.Id) continue;
                if (plate.LocationId != line.DestinationLocationId || plate.IsVoided || plate.Quantity <= 0) continue;
                selected.Add(new(plate.Id, plate.Quantity, plate.Version));
            }
            var needs = !evidence || ambiguous || (!recovered && originallyIdentified != line.Quantity);
            result.Add(new(line.Id, line.MovementId, line.DestinationLocationId!.Value, line.ProductId,
                line.Product.Sku, line.Product.Description ?? "", line.Unit.Code, line.SourceLocation?.Code ?? "Entrada",
                line.Movement.Reference, line.Movement.OccurredAt, line.Quantity,
                needs ? null : selected.Sum(p => p.Quantity), EncodeVersion(selected), needs));
        }
        return result;
    }

    private static bool IsIngress(PalletPlateEvent e, Guid staging)
    {
        var after = JsonSerializer.Deserialize<PalletState>(e.After)!;
        if (after.LocationId != staging || after.Quantity <= 0 || after.IsVoided) return false;
        var before = JsonSerializer.Deserialize<PalletState>(e.Before)!;
        return e.Kind is "StagingEntryIdentification" or "StagingSplitChild" ||
            (e.Kind is "Entry" or "Transfer" &&
                (before.LocationId != staging || before.IsVoided || after.Quantity > before.Quantity));
    }

    private static bool IsMixed(PalletPlateEvent e, Guid staging)
    {
        if (e.Kind is not ("Transfer" or "Consolidation" or "ConsolidationPrimary" or "Identification" or "Adjustment")) return false;
        var before = JsonSerializer.Deserialize<PalletState>(e.Before)!;
        var after = JsonSerializer.Deserialize<PalletState>(e.After)!;
        if (e.Kind != "Transfer") return before.LocationId == staging || after.LocationId == staging;
        return before.LocationId == staging && after.LocationId == staging && !before.IsVoided &&
            before.Quantity > 0 && after.Quantity > before.Quantity;
    }
}
