using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record IncidentFilter(string? Search = null, string? Status = null, MaterialIncidentScope? Scope = null, MaterialIncidentKind? Kind = null,
    Guid? ProductId = null, Guid? LocationId = null, Guid? PlateId = null, Guid? ArrivalLineId = null, string? Relation = null, int Page = 1, string? ProductCode = null, string? LocationCode = null);
public sealed record IncidentPage(IReadOnlyList<MaterialIncident> Items, bool HasMore);
public sealed record IncidentLinkContext(Guid Key, Guid ProductId, Guid? LocationId = null, Guid? PlateId = null, Guid? ArrivalLineId = null);
public sealed record IncidentCounts(int Reported, int Related);

public sealed class MaterialIncidentQuery(WarehouseDbContext db, MaterialIncidentService service)
{
    public async Task<IncidentPage> ListAsync(IncidentFilter filter, CancellationToken ct = default)
    {
        var query = db.MaterialIncidents.AsNoTracking();
        query = filter.Status?.ToLowerInvariant() switch {
            "all" => query, "open" => query.Where(i => i.Status == MaterialIncidentStatus.Open),
            "reviewing" => query.Where(i => i.Status == MaterialIncidentStatus.Reviewing), "resolved" => query.Where(i => i.Status == MaterialIncidentStatus.Resolved),
            "voided" => query.Where(i => i.Status == MaterialIncidentStatus.Voided), _ => query.Where(i => i.Status == MaterialIncidentStatus.Open || i.Status == MaterialIncidentStatus.Reviewing)
        };
        if (filter.Scope.HasValue) query = query.Where(i => i.Scope == filter.Scope);
        if (filter.Kind.HasValue) query = query.Where(i => i.Kind == filter.Kind);
        if (filter.ProductId.HasValue) query = query.Where(i => i.ProductId == filter.ProductId);
        if (!string.IsNullOrWhiteSpace(filter.ProductCode)) { var code = filter.ProductCode.Trim().ToUpperInvariant(); query = query.Where(i => i.Product.Sku.ToUpper().Contains(code)); }
        if (!string.IsNullOrWhiteSpace(filter.LocationCode)) { var code = filter.LocationCode.Trim().ToUpperInvariant(); query = query.Where(i => i.DetectionLocation.Code.ToUpper().Contains(code)); }
        if (filter.ArrivalLineId.HasValue) query = query.Where(i => i.ArrivalLineId == filter.ArrivalLineId);
        if (filter.PlateId is Guid plate)
        {
            var ids = await service.AncestorsAsync([plate], ct);
            query = query.Where(i => i.PlateId.HasValue && ids.Contains(i.PlateId.Value));
        }
        if (filter.LocationId is Guid location)
        {
            if (filter.Relation == "current")
            {
                var plates = await db.PalletPlates.Where(p => p.LocationId == location && !p.IsVoided && p.Quantity != 0).Select(p => p.Id).ToArrayAsync(ct);
                var ids = await service.AncestorsAsync(plates, ct);
                query = query.Where(i => i.PlateId.HasValue && ids.Contains(i.PlateId.Value) && i.DetectionLocationId != location);
            }
            else query = query.Where(i => i.DetectionLocationId == location);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            if (Guid.TryParse(term.Replace("INC-", "", StringComparison.Ordinal).Replace("PLT-", "", StringComparison.Ordinal), out var id))
                query = query.Where(i => i.Id == id || i.PlateId == id);
            else if (term.StartsWith("INC-", StringComparison.Ordinal))
            { var fragment = term[4..].Replace("-", "", StringComparison.Ordinal); query = query.Where(i => i.Id.ToString().Replace("-", "").ToUpper().Contains(fragment)); }
            else if (term.StartsWith("PLT-", StringComparison.Ordinal))
            { var fragment = term[4..].Replace("-", "", StringComparison.Ordinal); query = query.Where(i => i.PlateId.HasValue && i.PlateId.Value.ToString().Replace("-", "").ToUpper().Contains(fragment)); }
            else query = query.Where(i => i.Product.Sku.ToUpper().Contains(term) || i.DetectionLocation.Code.ToUpper().Contains(term) ||
                db.InventoryMovementLines.Any(l => l.Id == i.ArrivalLineId && l.Movement.Reference != null && l.Movement.Reference.ToUpper().Contains(term)));
        }
        var rows = await query.Include(i => i.Product).Include(i => i.DetectionLocation).Include(i => i.Unit)
            .OrderBy(i => i.ReportedAt).ThenBy(i => i.Id).Skip((Math.Clamp(filter.Page, 1, 100000) - 1) * 25).Take(26).ToListAsync(ct);
        return new(rows.Take(25).ToArray(), rows.Count > 25);
    }

    public async Task<IReadOnlyDictionary<Guid, IncidentCounts>> CountsAsync(IReadOnlyList<IncidentLinkContext> contexts, CancellationToken ct = default)
    {
        if (contexts.Count == 0) return new Dictionary<Guid, IncidentCounts>();
        var products = contexts.Select(c => c.ProductId).Distinct().ToArray();
        var incidents = await db.MaterialIncidents.AsNoTracking().Where(i => products.Contains(i.ProductId) &&
            (i.Status == MaterialIncidentStatus.Open || i.Status == MaterialIncidentStatus.Reviewing))
            .Select(i => new { i.Id, i.ProductId, i.DetectionLocationId, i.PlateId, i.ArrivalLineId }).ToListAsync(ct);
        var locations = contexts.Where(c => c.LocationId.HasValue).Select(c => c.LocationId!.Value).Distinct().ToArray();
        var plates = await db.PalletPlates.AsNoTracking().Where(p => products.Contains(p.ProductId) && locations.Contains(p.LocationId) && !p.IsVoided && p.Quantity != 0).Select(p => new { p.Id, p.ProductId, p.LocationId }).ToListAsync(ct);
        // Resolve split edges in batches, not one query per card.
        var seeds = plates.Select(p => p.Id).Concat(contexts.Where(c => c.PlateId.HasValue).Select(c => c.PlateId!.Value)).ToArray();
        var edges = await MaterialIncidentLineage.LoadAsync(db, seeds, false, ct);
        HashSet<Guid> Expand(IEnumerable<Guid> input) => MaterialIncidentLineage.Expand(input, edges);
        return contexts.ToDictionary(c => c.Key, c => {
            var ids = Expand(c.PlateId.HasValue ? [c.PlateId.Value] : plates.Where(p => p.ProductId == c.ProductId && p.LocationId == c.LocationId).Select(p => p.Id));
            var direct = incidents.Where(i => i.ProductId == c.ProductId && (c.ArrivalLineId.HasValue ? i.ArrivalLineId == c.ArrivalLineId : c.PlateId.HasValue ? i.PlateId == c.PlateId : i.DetectionLocationId == c.LocationId)).Select(i => i.Id).ToHashSet();
            var related = incidents.Where(i => i.ProductId == c.ProductId && i.PlateId.HasValue && ids.Contains(i.PlateId.Value) && !direct.Contains(i.Id)).Select(i => i.Id).Distinct().Count();
            return new IncidentCounts(direct.Count, related);
        });
    }
}
