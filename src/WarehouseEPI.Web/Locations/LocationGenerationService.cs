using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;

namespace WarehouseEPI.Web.Locations;

public sealed class LocationGenerationService(WarehouseDbContext dbContext, LocationGenerationPreviewStore store)
{
    private const int MaxBlocks = 50;
    private const int MaxCandidates = 5000;

    public async Task<LocationGenerationPreview> PrepareAsync(string? manifest, Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var source = (manifest ?? string.Empty).Trim();
        var errors = new List<string>();
        var candidates = Parse(source, errors);
        var codes = candidates.Select(row => row.Code).ToList();
        var existing = codes.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) :
            (await dbContext.Locations.AsNoTracking().Where(location => codes.Contains(location.Code))
                .Select(location => location.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var rows = candidates.Select(row => row with { Exists = existing.Contains(row.Code) }).ToList();
        if (existing.Count > 0) errors.Add($"Ya existen {existing.Count} ubicaciones de la preparación.");
        return store.Save(ownerUserId, source, rows, errors);
    }

    public bool TryGetPreview(string token, Guid ownerUserId, out LocationGenerationPreview? preview) =>
        store.TryGet(token, ownerUserId, out preview);

    public async Task<LocationGenerationConfirmation> ConfirmAsync(string token, Guid ownerUserId,
        IReadOnlyCollection<string>? selectedCodes, CancellationToken cancellationToken = default)
    {
        await using var generationLock = await store.LockAsync(token, cancellationToken);
        if (!store.TryGet(token, ownerUserId, out var preview) || preview is null)
            return new(false, 0, "La vista previa expiró, ya fue utilizada o pertenece a otro administrador.");
        if (!preview.CanConfirm)
            return new(false, 0, "La vista previa contiene errores bloqueantes.");

        var selected = (selectedCodes ?? []).Select(LocationNormalization.NormalizeCode)
            .ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return new(false, 0, "Selecciona al menos una ubicación validada.");
        if (selected.Except(preview.Rows.Select(row => row.Code)).Any())
            return new(false, 0, "La selección no corresponde a esta vista previa.");
        var rows = preview.Rows.Where(row => selected.Contains(row.Code)).ToList();
        if (await dbContext.Locations.AsNoTracking().AnyAsync(location => selected.Contains(location.Code), cancellationToken))
            return new(false, 0, "Una ubicación fue creada después de la vista previa. No se insertó ninguna.");

        var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            dbContext.Locations.AddRange(rows.Select(row => new Location
            {
                Code = row.Code, Kind = LocationKind.Rack, RowCode = row.RowCode,
                RackNumber = row.RackNumber, PalletNumber = row.PalletNumber
            }));
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            store.Remove(token);
            return new(true, rows.Count);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return new(false, 0, "La base de datos rechazó la carga. No se insertó ninguna ubicación.");
        }
        finally { if (transaction is not null) await transaction.DisposeAsync(); }
    }

    private static List<LocationGenerationRow> Parse(string manifest, List<string> errors)
    {
        var rows = new List<LocationGenerationRow>();
        var lines = manifest.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) errors.Add("Agrega al menos un bloque de fila, racks y pallets.");
        if (lines.Length > MaxBlocks) errors.Add($"La preparación admite como máximo {MaxBlocks} bloques.");
        foreach (var (line, index) in lines.Take(MaxBlocks).Select((value, index) => (value, index)))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4 || !short.TryParse(parts[1], out var start) || !short.TryParse(parts[2], out var end))
            { errors.Add($"Línea {index + 1}: usa Fila,RackInicial,RackFinal,Pallets."); continue; }
            var rowCode = LocationNormalization.NormalizeRowCode(parts[0]);
            if (!LocationNormalization.IsValidRowCode(rowCode)) { errors.Add($"Línea {index + 1}: la fila debe ser una letra."); continue; }
            if (start <= 0 || end < start) { errors.Add($"Línea {index + 1}: el rango de racks no es válido."); continue; }
            var pallets = ParsePallets(parts[3]);
            if (pallets.Count == 0) { errors.Add($"Línea {index + 1}: indica pallets entre 1 y 9."); continue; }
            foreach (var rack in Enumerable.Range(start, end - start + 1))
                foreach (var pallet in pallets)
                    rows.Add(new(LocationNormalization.BuildRackCode(rowCode, (short)rack, pallet), rowCode, (short)rack, pallet, false));
            if (rows.Count > MaxCandidates) { errors.Add($"La preparación supera {MaxCandidates:N0} ubicaciones."); break; }
        }
        var duplicateCount = rows.GroupBy(row => row.Code).Count(group => group.Count() > 1);
        if (duplicateCount > 0) errors.Add($"Hay {duplicateCount} códigos repetidos entre los bloques.");
        return rows.DistinctBy(row => row.Code).Take(MaxCandidates).OrderBy(row => row.RowCode)
            .ThenBy(row => row.RackNumber).ThenBy(row => row.PalletNumber).ToList();
    }

    private static SortedSet<short> ParsePallets(string value)
    {
        var result = new SortedSet<short>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 1 && short.TryParse(range[0], out var single) && single is >= 1 and <= 9) result.Add(single);
            else if (range.Length == 2 && short.TryParse(range[0], out var start) && short.TryParse(range[1], out var end) && start >= 1 && end <= 9 && end >= start)
                foreach (var number in Enumerable.Range(start, end - start + 1)) result.Add((short)number);
            else return [];
        }
        return result;
    }
}
