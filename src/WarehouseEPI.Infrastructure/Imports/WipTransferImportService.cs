using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Inventory;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;
using WarehouseEPI.Infrastructure.Settings;

namespace WarehouseEPI.Infrastructure.Imports;

public sealed record WipTransferResolution(Guid? SourceId = null, Guid? DestinationId = null,
    bool Excluded = false, string? ExclusionReason = null, bool AutomaticSource = false);
public sealed record WipTransferOrigin(Guid Id, string Code, decimal Quantity);
public sealed record WipTransferReviewRow(WipTransferSourceRow Source, WipTransferResolution Resolution,
    Guid? ProductId, Guid? ExistingMovementId, IReadOnlyList<string> Errors, IReadOnlyList<WipTransferOrigin> SourceOptions)
{
    public bool Pending => !Resolution.Excluded && ExistingMovementId is null;
    public string? RecordedSource { get; init; }
    public string? RecordedDestination { get; init; }
    public DateTimeOffset? RecordedAt { get; init; }
    public Guid? DocumentId { get; init; }
}
public sealed record WipTransferStockEffect(string Sku, string Unit, string Location, decimal Before, decimal Change)
{
    public decimal After => Before + Change;
}
public sealed record WipTransferReview(IReadOnlyList<WipTransferReviewRow> Rows, IReadOnlyList<Location> Origins,
    IReadOnlyList<Location> Destinations, IReadOnlyList<WipTransferStockEffect> Effects)
{
    public bool CanConfirm => Rows.Any(x => x.Pending) && Rows.All(x => !x.Pending || x.Errors.Count == 0);
}
public sealed record WipTransferImportResult(bool Success, int Imported = 0, int AlreadyImported = 0, string? Error = null);

public sealed class WipTransferImportService(WarehouseDbContext db, InventoryMovementService movements,
    UserPinService pins, WarehouseClock clock, TimeProvider timeProvider, ILogger<WipTransferImportService>? logger = null)
{
    private static readonly Action<ILogger, string, Exception?> LogRollback = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(57001, "WipImportRollback"), "WIP transfer import rolled back for file hash {FileHash}");

    public async Task<WipTransferReview> ReviewAsync(WipTransferFile file,
        IReadOnlyDictionary<int, WipTransferResolution> resolutions, bool suggestLocations = false, CancellationToken ct = default)
    {
        var skus = file.Rows.Select(x => x.Sku).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Include(x => x.BaseUnit)
            .Where(x => skus.Contains(x.Sku)).ToDictionaryAsync(x => x.Sku, ct);
        var locations = await db.Locations.AsNoTracking().Where(x => x.IsActive && !x.IsBlocked && x.IsPhysicallyPresent)
            .OrderBy(x => x.Code).ToListAsync(ct);
        var origins = locations.Where(x => x.Kind == LocationKind.Rack && !x.IsWip).ToArray();
        var destinations = locations.Where(x => x.IsWip).ToArray();
        var productIds = products.Values.Select(x => x.Id).ToArray();
        var balances = await db.InventoryBalances.AsNoTracking().Where(x => productIds.Contains(x.ProductId))
            .GroupBy(x => new { x.ProductId, x.LocationId })
            .Select(g => new { g.Key.ProductId, g.Key.LocationId, Quantity = g.Sum(x => x.Quantity) }).ToListAsync(ct);
        var ids = file.Rows.Select(x => x.OperationId).ToArray();
        var existing = await db.InventoryMovements.AsNoTracking().Where(x => ids.Contains(x.OperationId))
            .Include(x => x.OperationalArea).Include(x => x.Lines).ThenInclude(x => x.SourceLocation)
            .Include(x => x.Lines).ThenInclude(x => x.DestinationLocation)
            .ToDictionaryAsync(x => x.OperationId, ct);
        var existingIds = existing.Values.Select(x => x.Id).ToArray();
        var recordedDates = await clock.ConvertManyAsync(existing.Values.Select(x => x.RecordedAt), ct);
        var documents = await db.WipDocuments.AsNoTracking()
            .Where(x => x.MovementLine != null && existingIds.Contains(x.MovementLine.MovementId))
            .Select(x => new { x.Id, x.MovementLineId }).ToDictionaryAsync(x => x.MovementLineId!.Value, x => x.Id, ct);
        var today = await clock.GetDateAsync(timeProvider.GetUtcNow(), ct);
        var rows = new List<WipTransferReviewRow>();
        foreach (var source in file.Rows)
        {
            var resolution = resolutions.GetValueOrDefault(source.Number) ?? new();
            products.TryGetValue(source.Sku, out var product);
            if (existing.TryGetValue(source.OperationId, out var recorded))
            {
                // Reopening a file is a read of the original delivery, not a new location proposal.
                // Its source may now be empty or inactive; never substitute today's stock locations.
                var line = recorded.Lines.SingleOrDefault(x => x.ProductId == product?.Id);
                rows.Add(new(source, new(line?.SourceLocationId, recorded.OperationalAreaId ?? line?.DestinationLocationId),
                    product?.Id, recorded.Id, [], [])
                {
                    RecordedSource = line?.SourceLocation?.Code,
                    RecordedDestination = recorded.OperationalArea?.Code ?? line?.DestinationLocation?.Code,
                    RecordedAt = recordedDates[recorded.RecordedAt],
                    DocumentId = line is not null && documents.TryGetValue(line.Id, out var documentId) ? documentId : null
                });
                continue;
            }
            var sourceOptions = product is not { IsActive: true, BaseUnit.IsActive: true } ? [] : origins
                .Where(location => balances.Any(x => x.ProductId == product.Id && x.LocationId == location.Id && x.Quantity > 0))
                .Select(location => new WipTransferOrigin(location.Id, location.Code,
                    balances.Where(x => x.ProductId == product.Id && x.LocationId == location.Id).Sum(x => x.Quantity))).ToArray();
            if (suggestLocations && resolution.SourceId is null && sourceOptions.Length == 1)
                resolution = resolution with { SourceId = sourceOptions[0].Id, AutomaticSource = true };
            if (suggestLocations && resolution.DestinationId is null && source.Area.Length > 0)
            {
                var matches = destinations.Where(x => WipTransferSpreadsheetReader.Normalize(x.Code) == WipTransferSpreadsheetReader.Normalize(source.Area)
                    || WipTransferSpreadsheetReader.Normalize(x.Description ?? "") == WipTransferSpreadsheetReader.Normalize(source.Area)).ToArray();
                if (matches.Length == 1) resolution = resolution with { DestinationId = matches[0].Id };
            }
            var errors = source.Errors.ToList();
            if (product is null || !product.IsActive) errors.Add("El producto no existe o está inactivo. Corrige el catálogo o el archivo y vuelve a analizar.");
            else
            {
                if (!product.BaseUnit.IsActive || !string.Equals(product.BaseUnit.Code, source.Unit, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"La unidad del archivo ({source.Unit}) no coincide con la unidad base activa ({product.BaseUnit.Code}). No se realizan conversiones.");
                if (!product.BaseUnit.AllowsDecimals && source.Quantity is decimal qty && decimal.Truncate(qty) != qty)
                    errors.Add("La unidad del producto no permite decimales.");
            }
            if (source.Date > today) errors.Add("La fecha de entrega está en el futuro. Corrige el archivo.");
            if (!sourceOptions.Any(x => x.Id == resolution.SourceId)) errors.Add(sourceOptions.Length > 1
                ? "El producto tiene varias ubicaciones. Selecciona el origen antes de importar."
                : "Selecciona un rack de origen con stock positivo de este producto, fuera de WIP.");
            if (!destinations.Any(x => x.Id == resolution.DestinationId)) errors.Add("Selecciona un área WIP disponible.");
            if (resolution.Excluded && string.IsNullOrWhiteSpace(resolution.ExclusionReason))
                errors.Add("Indica por qué se excluye la fila.");
            rows.Add(new(source, resolution, product?.Id, null, errors, sourceOptions));
        }
        var changes = rows.Where(x => x.Pending && x.Errors.Count == 0)
            .Select(x => (Row: x, Id: x.Resolution.SourceId!.Value, Delta: -x.Source.Quantity!.Value));
        var effects = changes.GroupBy(x => new { x.Row.ProductId, x.Id, x.Row.Source.Sku, x.Row.Source.Unit })
            .Select(g => new WipTransferStockEffect(g.Key.Sku, g.Key.Unit, locations.Single(x => x.Id == g.Key.Id).Code,
                balances.Where(x => x.ProductId == g.Key.ProductId && x.LocationId == g.Key.Id).Sum(x => x.Quantity), g.Sum(x => x.Delta)))
            .OrderBy(x => x.Sku).ThenBy(x => x.Location).ToArray();
        return new(rows, origins, destinations, effects);
    }

    public async Task<WipTransferImportResult> ConfirmAsync(WipTransferFile file,
        IReadOnlyDictionary<int, WipTransferResolution> resolutions, string pin, bool reviewedRepeatedRows,
        bool approveSharing, CancellationToken ct = default)
    {
        var actor = await pins.AuthenticateAsync(pin, ct);
        if (actor is null || actor.Role.Code != "ADMIN") return new(false, Error: "Confirma con un NIP ADMIN válido.");
        if (!db.Database.IsRelational()) return new(false, Error: "La importación requiere una base de datos transaccional.");
        // Resolve settings before opening a transaction (settings fallback may query a missing table).
        var dates = new Dictionary<DateOnly, DateTimeOffset>();
        foreach (var date in file.Rows.Where(x => x.Date.HasValue && x.Date.Value.Year is >= 2000 and <= 2100).Select(x => x.Date!.Value).Distinct())
            dates[date] = (await clock.GetUtcIntervalAsync(date, null, ct)).FromInclusive!.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Serializes overlapping files across application instances. The movement OperationId
            // unique constraint is the durable deduplication boundary, including after a restart.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(570018426)", ct);
            var review = await ReviewAsync(file, resolutions, ct: ct);
            if (review.Rows.Any(x => x.Resolution.Excluded && string.IsNullOrWhiteSpace(x.Resolution.ExclusionReason)))
                return new(false, Error: "Indica un motivo para cada fila excluida.");
            if (review.Rows.Any(x => x.Pending && x.Errors.Count > 0))
                return new(false, Error: "Hay filas pendientes de corregir. Revisa la vista previa.");
            var pending = review.Rows.Where(x => x.Pending).OrderBy(x => x.Source.Date).ThenBy(x => x.Source.Number).ToArray();
            if (pending.Any(x => x.Source.Repeated) && !reviewedRepeatedRows)
                return new(false, Error: "Revisa las filas idénticas y confirma si representan entregas distintas.");
            var locations = pending.SelectMany(x => new[] { x.Resolution.SourceId!.Value, x.Resolution.DestinationId!.Value }).Distinct().Order().ToArray();
            await InventoryMovementStore.LockLocationsAsync(locations, transaction, ct);
            var imported = 0;
            foreach (var row in pending)
            {
                var source = row.Source;
                var command = new InventoryMovementCommand(source.OperationId, InventoryMovementType.Exit, "",
                    [new(row.ProductId!.Value, source.Quantity!.Value, row.Resolution.SourceId, AutomaticPalletHandling: true)],
                    Reference: $"WIP import · {file.Name}"[..Math.Min(120, $"WIP import · {file.Name}".Length)],
                    Notes: $"TRANSFER LOG fila {source.Number}; fecha {source.Date:yyyy-MM-dd}; área original: {source.Area[..Math.Min(100, source.Area.Length)]}; SHA256 {file.Hash}; repetidas revisadas: {reviewedRepeatedRows}; compartir: {approveSharing}",
                    ApprovedSharedAssignments: approveSharing ? [new(row.ProductId.Value, row.Resolution.SourceId!.Value)] : [],
                    Purpose: InventoryMovementPurpose.ProductionIssue, OperationalAreaId: row.Resolution.DestinationId);
                var result = await movements.ConfirmImportedIssueAsync(command, actor, dates[source.Date!.Value], ct);
                if (result.Status != InventoryMovementStatus.Success)
                {
                    await transaction.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    var message = result.Status == InventoryMovementStatus.RequiresLocationSharingConfirmation
                        ? "El origen contiene otros productos. Revisa el origen o autoriza compartir ubicación."
                        : string.Join(" ", result.ValidationErrors.DefaultIfEmpty("El inventario cambió. Revisa y vuelve a confirmar."));
                    return new(false, Error: $"Fila {source.Number}: {message} No se importó ninguna fila.");
                }
                imported++;
            }
            await transaction.CommitAsync(ct);
            return new(true, imported, review.Rows.Count(x => x.ExistingMovementId.HasValue));
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            if (logger is not null) LogRollback(logger, file.Hash, exception);
            return new(false, Error: "No se pudo completar la importación. No se guardó ninguna fila. Revisa las existencias y vuelve a analizar.");
        }
    }
}
