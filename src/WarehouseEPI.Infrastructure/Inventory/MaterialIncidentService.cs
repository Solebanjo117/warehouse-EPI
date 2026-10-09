using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Labels;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record IncidentContextRequest(Guid? ArrivalLineId = null, Guid? PlateId = null, Guid? ProductId = null, Guid? LocationId = null);
public sealed record IncidentContext(Guid ProductId, short UnitId, string Sku, string Unit, bool AllowsDecimals,
    Guid LocationId, string Location, Guid? PlateId, Guid? ArrivalLineId, string State, decimal? ObservedQuantity, long? PlateVersion, string Token,
    string? StockVersion = null, Guid? ActualPlateLocationId = null, string? ActualPlateLocation = null);
public sealed record IncidentPhotoInput(string Name, string ContentType, byte[] Content);
public sealed record IncidentReport(Guid OperationId, IncidentContextRequest Context, string ContextToken, MaterialIncidentScope Scope,
    MaterialIncidentKind Kind, MaterialIncidentDifference Difference, decimal? Quantity, string Description, string Pin, IReadOnlyList<IncidentPhotoInput> Photos);
public sealed record IncidentFollowUp(Guid OperationId, Guid IncidentId, long ExpectedVersion, string Action,
    string Comment, Guid? CorrectionId, string Pin, IReadOnlyList<IncidentPhotoInput> Photos);
public sealed record IncidentResult(Guid? Id = null, string? Error = null, bool ContextChanged = false);

public sealed class MaterialIncidentService(WarehouseDbContext db, UserPinService pins, TimeProvider time)
{
    public const int MaxPhotoBytes = 5 * 1024 * 1024;
    public const string InvalidContext = "El contexto del material no es válido. Consulta nuevamente.";
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string PhotoHash(IncidentPhotoInput photo) => Convert.ToHexString(SHA256.HashData(photo.Content));

    public IQueryable<Product> ProductsAt(Guid locationId) => db.Products.AsNoTracking().Where(p =>
        db.InventoryBalances.Any(b => b.ProductId == p.Id && b.LocationId == locationId) ||
        db.ProductLocationAssignments.Any(a => a.ProductId == p.Id && a.LocationId == locationId) ||
        db.InventoryMovementLines.Any(l => l.ProductId == p.Id && (l.SourceLocationId == locationId || l.DestinationLocationId == locationId)));

    public async Task<IncidentContext?> ContextAsync(IncidentContextRequest request, CancellationToken ct = default)
    {
        var productId = request.ProductId; var locationId = request.LocationId; var lineId = request.ArrivalLineId;
        InventoryMovementLine? arrival = null;
        if (lineId.HasValue)
        {
            arrival = await db.InventoryMovementLines.AsNoTracking().Include(l => l.Movement).Include(l => l.DestinationLocation)
                .SingleOrDefaultAsync(l => l.Id == lineId, ct);
            if (arrival?.DestinationLocation is not { Code: "STAGING", Kind: LocationKind.Area, OperationalRole: not LocationOperationalRole.Wip } ||
                arrival.Movement.Type is not (InventoryMovementType.Entry or InventoryMovementType.Transfer) ||
                (productId.HasValue && productId != arrival.ProductId)) return null;
            productId = arrival.ProductId;
            locationId ??= arrival.DestinationLocationId;
        }
        PalletPlate? plate = null;
        if (request.PlateId is Guid plateId)
        {
            plate = await db.PalletPlates.AsNoTracking().SingleOrDefaultAsync(p => p.Id == plateId, ct);
            if (plate is null || (productId.HasValue && plate.ProductId != productId)) return null;
            productId = plate.ProductId; locationId ??= plate.LocationId;
            if (lineId.HasValue && !await db.PalletPlateEvents.AnyAsync(e => e.PlateId == plateId && e.MovementLineId == lineId, ct)) return null;
            if (!lineId.HasValue) lineId = await ReceiptLineAsync(plateId, ct);
        }
        if (!productId.HasValue || !locationId.HasValue) return null;
        var product = await db.Products.AsNoTracking().Include(p => p.BaseUnit).SingleOrDefaultAsync(p => p.Id == productId, ct);
        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == locationId, ct);
        if (product is null || location is null || location.OperationalRole == LocationOperationalRole.Wip) return null;
        if (plate is null && arrival is null && !await ProductsAt(location.Id).AnyAsync(p => p.Id == product.Id, ct)) return null;
        if (plate is null && arrival is not null && location.Id != arrival.DestinationLocationId) return null;
        var balances = plate is null ? await db.InventoryBalances.AsNoTracking().Where(b => b.ProductId == product.Id && b.LocationId == location.Id)
            .OrderBy(b => b.LotId).Select(b => new { b.LotId, b.Quantity, b.Version }).ToArrayAsync(ct) : null;
        decimal? quantity = plate?.Quantity ?? balances!.Sum(b => b.Quantity);
        var state = plate is not null ? await PlateStateAsync(plate, ct) : !location.IsActive ? "Inactiva" : location.IsBlocked ? "Bloqueada" : "Disponible";
        var stockVersion = balances is null ? null : Hash(JsonSerializer.Serialize(balances));
        if (arrival is not null && plate is null)
        {
            var row = await new StagingArrivalQuery(db).GetAsync(arrival.Id, ct);
            quantity = row is null ? arrival.Quantity : row.Pending;
            state = row is null ? "Movimiento corregido" : row.NeedsIdentification ? "Requiere identificación" : row.Pending == 0 ? "Agotada" : "Disponible";
            stockVersion = Hash(JsonSerializer.Serialize(new { Balance = stockVersion, Arrival = row?.Version, state }));
        }
        var context = new IncidentContext(product.Id, product.BaseUnitId, product.Sku, product.BaseUnit.Code, product.BaseUnit.AllowsDecimals,
            location.Id, location.Code, plate?.Id, lineId, state, quantity, plate?.Version, "", stockVersion, plate?.LocationId,
            plate is null ? null : plate.LocationId == location.Id ? location.Code : await db.Locations.Where(l => l.Id == plate.LocationId).Select(l => l.Code).SingleAsync(ct));
        // Include actual plate location even when detection location is historical.
        return context with { Token = Hash(JsonSerializer.Serialize(new { context, ActualLocation = plate?.LocationId })) };
    }

    public async Task<Guid?> ReceiptLineAsync(Guid plateId, CancellationToken ct)
    {
        var family = await AncestorsAsync([plateId], ct);
        var events = await db.PalletPlateEvents.AsNoTracking().Where(e => family.Contains(e.PlateId)).ToListAsync(ct);
        // Mixed/reconciled identities must never be attributed to an arbitrary receipt.
        if (events.Any(e => e.Kind is "Consolidation" or "ConsolidationPrimary" or "Adjustment" or "Identification" or "Reversal")) return null;
        // Receiving into an existing plate can mix origins; a matching SKU or origin movement is not proof.
        if (events.Any(e => e.Kind == "Transfer" && MaterialIncidentLineage.State(e.Before) is {} before &&
            MaterialIncidentLineage.State(e.After) is {} after && after.Quantity > before.Quantity && e.PlateVersion != 1)) return null;
        var ids = events.Where(e => e.MovementLineId.HasValue && e.Kind is "Entry" or "StagingEntryIdentification")
            .Select(e => e.MovementLineId!.Value).Distinct().ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }
    public async Task<string> PlateStateAsync(PalletPlate plate, CancellationToken ct) =>
        !plate.IsVoided && plate.Quantity == 0 && await db.PalletPlateEvents.AnyAsync(e => e.PlateId == plate.Id && e.Kind == "StagingSplitSource", ct)
            ? "Dividida" : plate.Status;

    public async Task<HashSet<Guid>> AncestorsAsync(IEnumerable<Guid> plateIds, CancellationToken ct)
    {
        var seeds = plateIds.ToArray();
        return MaterialIncidentLineage.Expand(seeds, await MaterialIncidentLineage.LoadAsync(db, seeds, false, ct));
    }

    public async Task<IncidentResult> ReportAsync(IncidentReport request, CancellationToken ct = default)
    {
        var user = await pins.AuthenticateAsync(request.Pin, ct);
        if (user is null || !RoleAccess.CanOperateWarehouse(user.Role.Code)) return new(Error: "NIP inválido o usuario sin permiso operativo.");
        var fingerprint = Hash(JsonSerializer.Serialize(new { request.OperationId, request.Context, request.ContextToken, request.Scope, request.Kind,
            request.Difference, request.Quantity, request.Description, User = user.Id, Photos = request.Photos.Select(p => new { p.Name, p.ContentType, Hash = PhotoHash(p) }) }));
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            if (tx is not null) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({request.OperationId.ToString()}, 0))", ct);
            var retry = await RetryAsync(request.OperationId, fingerprint, ct); if (retry is not null) return retry;
            if (request.Context.PlateId is Guid plateId && tx is not null)
            {
                var location = await db.PalletPlates.Where(p => p.Id == plateId).Select(p => (Guid?)p.LocationId).SingleOrDefaultAsync(ct);
                if (location.HasValue) await InventoryMovementStore.LockLocationsAsync([location.Value], tx, ct);
            }
            var context = await ContextAsync(request.Context, ct);
            if (context is null) return new(Error: InvalidContext);
            if (context.Token != request.ContextToken) return new(Error: "El material cambió. Revisa el contexto actualizado antes de confirmar.", ContextChanged: true);
            if (request.OperationId == Guid.Empty || !Enum.IsDefined(request.Scope) || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Difference) ||
                string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 2000 ||
                request.Quantity is decimal q && (q <= 0 || q > InventoryMovementRules.MaximumQuantity || decimal.Round(q, 4) != q || !context.AllowsDecimals && decimal.Truncate(q) != q))
                return new(Error: "Revisa la descripción, cantidad y clasificación de la incidencia.");
            var photoError = ValidatePhotos(request.Photos, 0); if (photoError is not null) return new(Error: photoError);
            var incident = new MaterialIncident { ProductId = context.ProductId, UnitId = context.UnitId, DetectionLocationId = context.LocationId,
                PlateId = context.PlateId, ArrivalLineId = context.ArrivalLineId, Scope = request.Scope, Kind = request.Kind,
                Difference = request.Kind == MaterialIncidentKind.QuantityDifference ? request.Difference : MaterialIncidentDifference.Undetermined,
                Quantity = request.Quantity, Description = request.Description.Trim(), Snapshot = JsonSerializer.Serialize(context), ReportedAt = time.GetUtcNow(), ReportedById = user.Id };
            db.MaterialIncidents.Add(incident);
            AddEvent(incident, request.OperationId, fingerprint, "Report", incident.Description, null, user.Id, request.Photos);
            await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
            return new(incident.Id);
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return await RetryAsync(request.OperationId, fingerprint, ct) ?? new(Error: "La incidencia cambió. Actualiza y revisa antes de confirmar.");
        }
    }

    public async Task<IncidentResult> FollowUpAsync(IncidentFollowUp request, CancellationToken ct = default)
    {
        var user = await pins.AuthenticateAsync(request.Pin, ct);
        if (user is null || !RoleAccess.CanOperateWarehouse(user.Role.Code)) return new(Error: "NIP inválido o usuario sin permiso operativo.");
        var fingerprint = Hash(JsonSerializer.Serialize(new { request.OperationId, request.IncidentId, request.ExpectedVersion, request.Action,
            request.Comment, request.CorrectionId, User = user.Id, Photos = request.Photos.Select(p => new { p.Name, p.ContentType, Hash = PhotoHash(p) }) }));
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            if (tx is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({request.OperationId.ToString()}, 0))", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({request.IncidentId.ToString()}, 1))", ct);
            }
            var retry = await RetryAsync(request.OperationId, fingerprint, ct); if (retry is not null) return retry;
            var item = await db.MaterialIncidents.SingleOrDefaultAsync(i => i.Id == request.IncidentId, ct);
            if (item is null) return new(Error: InvalidContext);
            if (item.Version != request.ExpectedVersion) return new(Error: "La incidencia cambió. Actualiza y revisa antes de confirmar.");
            if (request.OperationId == Guid.Empty || request.Comment.Length > 2000 ||
                request.Action is "Resolve" or "Void" or "Reopen" && string.IsNullOrWhiteSpace(request.Comment))
                return new(Error: "Escribe un comentario o motivo para el seguimiento.");
            var admin = user.Role.Code == RoleAccess.Admin;
            var closed = item.Status is MaterialIncidentStatus.Resolved or MaterialIncidentStatus.Voided;
            var next = request.Action switch
            {
                "Comment" when !closed => item.Status,
                "Review" when item.Status == MaterialIncidentStatus.Open => MaterialIncidentStatus.Reviewing,
                "Resolve" when item.Status == MaterialIncidentStatus.Reviewing && admin => MaterialIncidentStatus.Resolved,
                "Void" when !closed && admin => MaterialIncidentStatus.Voided,
                "Reopen" when closed && admin => MaterialIncidentStatus.Reviewing,
                _ => (MaterialIncidentStatus?)null
            };
            if (next is null) return new(Error: "Transición no permitida. Resolver, anular o reabrir requiere NIP ADMIN.");
            if (request.CorrectionId is Guid correctionId)
            {
                var correction = await db.InventoryMovementCorrections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == correctionId, ct);
                if (correction is null || !await (await CorrectionsForAsync(item, ct)).AnyAsync(c => c.Id == correctionId, ct))
                    return new(Error: "La corrección no corresponde al material de esta incidencia.");
            }
            var existingHashes = await db.MaterialIncidentPhotos.Where(p => p.IncidentId == item.Id).Select(p => p.Sha256).ToListAsync(ct);
            var photos = request.Photos.Where(p => !existingHashes.Contains(PhotoHash(p))).ToArray();
            var error = ValidatePhotos(photos, existingHashes.Count); if (error is not null) return new(Error: error);
            if (request.Action == "Comment" && string.IsNullOrWhiteSpace(request.Comment) && photos.Length == 0 && !request.CorrectionId.HasValue)
                return new(Error: "Escribe un comentario o motivo para el seguimiento.");
            item.Status = next.Value; item.Version++;
            AddEvent(item, request.OperationId, fingerprint, request.Action, request.Comment.Trim(), request.CorrectionId, user.Id, photos);
            await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
            return new(item.Id);
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return await RetryAsync(request.OperationId, fingerprint, ct) ?? new(Error: "La incidencia cambió. Actualiza y revisa antes de confirmar.");
        }
    }

    private async Task<IncidentResult?> RetryAsync(Guid operationId, string fingerprint, CancellationToken ct)
    {
        var prior = await db.MaterialIncidentEvents.AsNoTracking().SingleOrDefaultAsync(e => e.OperationId == operationId, ct);
        return prior is null ? null : prior.Fingerprint == fingerprint ? new(prior.IncidentId) : new(Error: "La operación ya fue usada con datos distintos.");
    }
    public async Task<IQueryable<InventoryMovementCorrection>> CorrectionsForAsync(MaterialIncident item, CancellationToken ct)
    {
        var arrivalMovementId = item.ArrivalLineId.HasValue ? await db.InventoryMovementLines.Where(l => l.Id == item.ArrivalLineId).Select(l => (Guid?)l.MovementId).SingleOrDefaultAsync(ct) : null;
        var plates = item.PlateId.HasValue ? await AncestorsAsync([item.PlateId.Value], ct) : [];
        return db.InventoryMovementCorrections.AsNoTracking().Where(c => db.InventoryMovementLines.Any(l => l.MovementId == c.OriginalMovementId && l.ProductId == item.ProductId) &&
            (arrivalMovementId.HasValue ? c.OriginalMovementId == arrivalMovementId : item.PlateId.HasValue
                ? db.PalletPlateEvents.Any(e => plates.Contains(e.PlateId) && e.MovementId == c.OriginalMovementId)
                : db.InventoryMovementLines.Any(l => l.MovementId == c.OriginalMovementId && l.ProductId == item.ProductId &&
                    (l.SourceLocationId == item.DetectionLocationId || l.DestinationLocationId == item.DetectionLocationId))));
    }
    private static string? ValidatePhotos(IReadOnlyList<IncidentPhotoInput> photos, int existing)
    {
        if (photos.Select(PhotoHash).Distinct().Count() + existing > 5) return "Máximo 5 fotografías por incidencia.";
        foreach (var photo in photos)
        {
            if (photo.Content.Length is < 1 or > MaxPhotoBytes) return "Cada fotografía debe pesar como máximo 5 MiB.";
            var size = LabelAssetService.ImageDimensions(photo.Content, photo.ContentType);
            if (size is null || size.Value.Width is < 1 or > 4096 || size.Value.Height is < 1 or > 4096) return "Selecciona fotografías PNG/JPEG válidas de hasta 4096 × 4096.";
        }
        return null;
    }
    private void AddEvent(MaterialIncident incident, Guid operationId, string fingerprint, string action, string comment, Guid? correctionId, Guid userId, IReadOnlyList<IncidentPhotoInput> photos)
    {
        var entry = new MaterialIncidentEvent { IncidentId = incident.Id, OperationId = operationId, Fingerprint = fingerprint, Version = incident.Version,
            Action = action, Comment = comment, CorrectionId = correctionId, Status = incident.Status, RecordedAt = time.GetUtcNow(), ResponsibleId = userId };
        db.MaterialIncidentEvents.Add(entry);
        foreach (var photo in photos.DistinctBy(PhotoHash))
        {
            var name = Path.GetFileName(photo.Name); if (name.Length > 120) name = name[..120];
            db.MaterialIncidentPhotos.Add(new() { IncidentId = incident.Id, EventId = entry.Id, Name = name, ContentType = photo.ContentType, Content = photo.Content, Sha256 = PhotoHash(photo) });
        }
    }
}
