using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record IncidentPlateEdge(Guid Parent, Guid Child);

/// <summary>Only explicit splits and transfers with one identifiable donor and one new child prove ancestry.</summary>
public static class MaterialIncidentLineage
{
    public static async Task<IReadOnlyList<IncidentPlateEdge>> LoadAsync(WarehouseDbContext db, IEnumerable<Guid> seeds, bool descendants, CancellationToken ct)
    {
        var visited = seeds.ToHashSet(); var pending = visited.ToArray(); var edges = new HashSet<IncidentPlateEdge>();
        while (pending.Length > 0)
        {
            var operations = await db.PalletPlateEvents.Where(e => pending.Contains(e.PlateId) &&
                (e.Kind == "StagingSplitChild" || e.Kind == "StagingSplitSource" || e.Kind == "Transfer"))
                .Select(e => e.OperationId).Distinct().ToArrayAsync(ct);
            var events = await db.PalletPlateEvents.AsNoTracking().Where(e => operations.Contains(e.OperationId)).ToListAsync(ct);
            foreach (var group in events.GroupBy(e => new { e.OperationId, e.MovementLineId }))
            {
                var sources = group.Where(e => e.Kind == "StagingSplitSource").ToArray();
                if (sources.Length == 1)
                    foreach (var child in group.Where(e => e.Kind == "StagingSplitChild")) edges.Add(new(sources[0].PlateId, child.PlateId));
                if (!group.Key.MovementLineId.HasValue) continue;
                var transfers = group.Where(e => e.Kind == "Transfer").Select(e => (Event: e, Before: State(e.Before), After: State(e.After))).ToArray();
                var donors = transfers.Where(e => e.Before is not null && e.After is not null && e.Before.Quantity > e.After.Quantity).ToArray();
                var recipients = transfers.Where(e => e.Before is not null && e.After is not null && e.After.Quantity > e.Before.Quantity).ToArray();
                if (donors.Length == 1 && recipients.Length == 1 && recipients[0].Before!.Quantity == 0 && recipients[0].Event.PlateVersion == 1 &&
                    donors[0].Before!.Quantity - donors[0].After!.Quantity == recipients[0].After!.Quantity)
                    edges.Add(new(donors[0].Event.PlateId, recipients[0].Event.PlateId));
            }
            var next = edges.Where(e => descendants ? visited.Contains(e.Parent) : visited.Contains(e.Child))
                .Select(e => descendants ? e.Child : e.Parent).Distinct().ToArray();
            pending = next.Where(visited.Add).ToArray();
        }
        return edges.ToArray();
    }

    public static HashSet<Guid> Expand(IEnumerable<Guid> seeds, IEnumerable<IncidentPlateEdge> edges, bool descendants = false)
    {
        var result = seeds.ToHashSet(); bool changed;
        do {
            changed = false;
            foreach (var edge in edges)
                if (result.Contains(descendants ? edge.Parent : edge.Child)) changed |= result.Add(descendants ? edge.Child : edge.Parent);
        } while (changed);
        return result;
    }
    internal static PalletState? State(string json)
    {
        try { return JsonSerializer.Deserialize<PalletState>(json); } catch (JsonException) { return null; }
    }
}
