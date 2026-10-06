using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public enum WipDefaultStatus { Success, InvalidPin, NotFound, ValidationFailed, ConcurrencyConflict, IdempotencyConflict }
public sealed record WipDefaultResult(WipDefaultStatus Status, IReadOnlyList<string>? Errors = null);
public sealed record WipDefaultChoice(string Key, string Label, string Type, string Description, bool IsAvailable, string? Warning = null);
public sealed record MaterialWipRuleInput(Guid StageId, string TargetKey);
public sealed record SaveMaterialWipDefaultsCommand(Guid OperationId, Guid ProductId, uint ExpectedVersion,
    IReadOnlyList<MaterialWipRuleInput> Rules, string Reason, string Pin);
public sealed record MaterialWipRuleView(Guid StageId, string Process, string TargetKey, string Target,
    string Type, bool IsAvailable, string? Warning);
public sealed record MaterialWipDefaultsView(uint Version, IReadOnlyList<MaterialWipRuleView> Rules,
    IReadOnlyList<ProductionStage> Processes, IReadOnlyList<ProductionMaterialWipRevision> Revisions);

public sealed class ProductionWipDefaultService(
    WarehouseDbContext db,
    UserPinService pins,
    TimeProvider timeProvider)
{
    public async Task<(uint Version, IReadOnlyList<ProductionStage> Processes)> GetSetupAsync(CancellationToken token = default) =>
        (await VersionAsync(token), await db.ProductionStages.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(token));
    public async Task<MaterialWipDefaultsView?> GetMaterialAsync(Guid productId, CancellationToken token = default)
    {
        if (!await db.Products.AsNoTracking().AnyAsync(x => x.Id == productId, token)) return null;
        var rules = await db.ProductionMaterialWipDefaults.AsNoTracking()
            .Include(x => x.ProductionStage).Include(x => x.Location)
            .Where(x => x.ProductId == productId).OrderBy(x => x.ProductionStage.Name).ToListAsync(token);
        var views = new List<MaterialWipRuleView>();
        foreach (var rule in rules)
        {
            var target = await DescribeAsync(rule.ProductionStageId, Key(rule.LocationId, rule.RowCode, rule.RackNumber), token);
            views.Add(new(rule.ProductionStageId, $"{rule.ProductionStage.Code} · {rule.ProductionStage.Name}",
                target.Key, target.Label, target.Type, target.IsAvailable, target.Warning));
        }
        var processes = await db.ProductionStages.AsNoTracking().Where(x => x.IsActive || rules.Select(r => r.ProductionStageId).Contains(x.Id))
            .OrderBy(x => x.Name).ToListAsync(token);
        var revisions = await db.ProductionMaterialWipRevisions.AsNoTracking().Include(x => x.AuthorizedByUser)
            .Where(x => x.ProductId == productId).OrderByDescending(x => x.RecordedAt).Take(20).ToListAsync(token);
        return new(await VersionAsync(token), views, processes, revisions);
    }

    public async Task<IReadOnlyList<WipDefaultChoice>> SearchAsync(Guid stageId, string? search, CancellationToken token = default)
    {
        var term = search?.Trim() ?? "";
        if (term.Length == 0 || !await db.ProductionStages.AsNoTracking().AnyAsync(x => x.Id == stageId && x.IsActive, token)) return [];
        var associations = await db.ProductionProcessWipTargets.AsNoTracking().Where(x => x.ProductionStageId == stageId).ToListAsync(token);
        return await SearchWithAssociationsAsync(associations, term, token);
    }

    public async Task<IReadOnlyList<WipDefaultChoice>> SearchProposedAsync(IReadOnlyCollection<string> targetKeys,
        string? search, CancellationToken token = default)
    {
        var term = search?.Trim() ?? "";
        if (term.Length == 0) return [];
        var associations = new List<ProductionProcessWipTarget>();
        foreach (var key in targetKeys.Distinct(StringComparer.Ordinal))
        {
            if (key.StartsWith("A:", StringComparison.Ordinal) && Guid.TryParse(key[2..], out var area))
                associations.Add(new() { LocationId = area });
            else if (key.StartsWith("F:", StringComparison.Ordinal) && key.Length > 2)
                associations.Add(new() { RowCode = key[2..].Trim().ToUpperInvariant() });
            else if (key.Split(':') is ["R", var row, var number] && short.TryParse(number, out var rack))
                associations.Add(new() { RowCode = row.Trim().ToUpperInvariant(), RackNumber = rack });
        }
        return await SearchWithAssociationsAsync(associations, term, token);
    }

    private async Task<IReadOnlyList<WipDefaultChoice>> SearchWithAssociationsAsync(
        IReadOnlyCollection<ProductionProcessWipTarget> associations, string term, CancellationToken token)
    {
        var locations = await db.Locations.AsNoTracking().Where(x => x.OperationalRole == LocationOperationalRole.Wip &&
                x.IsActive && x.IsPhysicallyPresent && !x.IsBlocked)
            .Where(x => x.Code.ToUpper().Contains(term.ToUpper()) || (x.Description != null && x.Description.ToUpper().Contains(term.ToUpper())))
            .OrderBy(x => x.Code).Take(80).ToListAsync(token);
        var result = new List<WipDefaultChoice>();
        foreach (var location in locations)
        {
            if (!Associated(location, associations)) continue;
            result.Add(new($"P:{location.Id}", location.Code, location.Kind == LocationKind.Area ? "Área WIP" : "Posición WIP",
                location.Description ?? (location.Kind == LocationKind.Area ? "Área de proceso" : "Posición exacta"), true));
        }
        var racks = locations.Where(x => x.Kind == LocationKind.Rack && x.RowCode != null && x.RackNumber != null)
            .GroupBy(x => new { x.RowCode, x.RackNumber });
        foreach (var rack in racks)
        {
            var all = await db.Locations.AsNoTracking().Where(x => x.Kind == LocationKind.Rack &&
                x.RowCode == rack.Key.RowCode && x.RackNumber == rack.Key.RackNumber).ToListAsync(token);
            var wip = all.Where(x => x.OperationalRole == LocationOperationalRole.Wip).ToArray();
            if (!wip.Any(x => x.IsOperational)) continue;
            var partial = wip.Any(x => !x.IsOperational);
            result.Add(new($"R:{rack.Key.RowCode}:{rack.Key.RackNumber}", $"{rack.Key.RowCode}-{rack.Key.RackNumber}",
                "Rack WIP", "La posición exacta se confirma al surtir", true, partial ? "Disponibilidad parcial" : null));
        }
        return result.DistinctBy(x => x.Key).OrderBy(x => x.Label).Take(20).ToArray();
    }

    public async Task<WipDefaultResult> SaveMaterialAsync(SaveMaterialWipDefaultsCommand command, CancellationToken token = default)
    {
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code != "ADMIN") return new(WipDefaultStatus.InvalidPin);
        if (command.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason))
            return Invalid("Indica el motivo del cambio.");
        var normalized = command.Rules.Select(x => new MaterialWipRuleInput(x.StageId, NormalizeKey(x.TargetKey)))
            .OrderBy(x => x.StageId).ToArray();
        if (normalized.Any(x => x.StageId == Guid.Empty || string.IsNullOrEmpty(x.TargetKey)))
            return Invalid("Cada regla requiere un proceso y un destino WIP válido.");
        if (normalized.GroupBy(x => x.StageId).Any(x => x.Count() > 1)) return Invalid("Cada proceso puede tener un solo destino habitual.");
        var fingerprint = Hash(JsonSerializer.Serialize(new { command.ProductId, command.ExpectedVersion, Rules = normalized, Reason = command.Reason.Trim() }));
        var prior = await db.ProductionMaterialWipRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
        if (prior is not null) return prior.RequestFingerprint == fingerprint ? new(WipDefaultStatus.Success) : new(WipDefaultStatus.IdempotencyConflict);
        var configuration = await db.ProductionProcessConfigurations.SingleOrDefaultAsync(x => x.Id == 1, token) ?? new ProductionProcessConfiguration();
        if (configuration.Version != command.ExpectedVersion) return new(WipDefaultStatus.ConcurrencyConflict);
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == command.ProductId, token);
        if (product is null) return new(WipDefaultStatus.NotFound);
        if (!product.IsActive) return Invalid("Activa el producto antes de configurar destinos WIP.");
        var existing = await db.ProductionMaterialWipDefaults.Where(x => x.ProductId == command.ProductId).ToListAsync(token);
        var errors = new List<string>();
        foreach (var item in normalized)
        {
            var unchanged = existing.Any(x => x.ProductionStageId == item.StageId && Key(x.LocationId, x.RowCode, x.RackNumber) == item.TargetKey);
            var choice = await DescribeAsync(item.StageId, item.TargetKey, token);
            if (!choice.IsAvailable && !unchanged) errors.Add($"{choice.Label}: {choice.Warning ?? "el destino no está disponible."}");
        }
        if (errors.Count > 0) return new(WipDefaultStatus.ValidationFailed, errors);
        var before = JsonSerializer.Serialize(existing.Select(x => new { x.ProductionStageId, Target = Key(x.LocationId, x.RowCode, x.RackNumber) }).OrderBy(x => x.ProductionStageId));
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            db.ProductionMaterialWipDefaults.RemoveRange(existing);
            var now = timeProvider.GetUtcNow();
            foreach (var item in normalized)
            {
                var parsed = Parse(item.TargetKey);
                db.ProductionMaterialWipDefaults.Add(new()
                {
                    ProductId = command.ProductId,
                    ProductionStageId = item.StageId,
                    LocationId = parsed.LocationId,
                    RowCode = parsed.RowCode,
                    RackNumber = parsed.RackNumber,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            if (db.Entry(configuration).State == EntityState.Detached) db.Add(configuration);
            configuration.Version++;
            db.ProductionMaterialWipRevisions.Add(new()
            {
                OperationId = command.OperationId,
                RequestFingerprint = fingerprint,
                ProductId = command.ProductId,
                AuthorizedByUserId = user.Id,
                Reason = command.Reason.Trim(),
                BeforeJson = before,
                AfterJson = JsonSerializer.Serialize(normalized),
                RecordedAt = now
            });
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(WipDefaultStatus.Success);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(WipDefaultStatus.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            prior = await db.ProductionMaterialWipRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            if (prior is not null)
                return prior.RequestFingerprint == fingerprint ? new(WipDefaultStatus.Success) : new(WipDefaultStatus.IdempotencyConflict);
            return Invalid("La configuración no pudo guardarse. Verifica que la actualización de base de datos esté aplicada.");
        }
    }

    public async Task<WipDefaultChoice> DescribeAsync(Guid stageId, string? key, CancellationToken token = default)
    {
        var stage = await db.ProductionStages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == stageId, token);
        if (stage is null) return new(key ?? "", "Proceso inexistente", "Destino WIP", "El proceso ya no existe", false, "El proceso ya no existe.");
        if (string.IsNullOrWhiteSpace(key)) return new("", "Sin destino", "Destino WIP", "Alternativa manual", true);
        var parsed = Parse(key);
        var associations = await db.ProductionProcessWipTargets.AsNoTracking().Where(x => x.ProductionStageId == stageId).ToListAsync(token);
        if (parsed.LocationId is Guid locationId)
        {
            var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == locationId, token);
            if (location is null) return new(key, "Ubicación retirada", "Destino WIP", "La ubicación ya no existe", false, "La ubicación ya no existe.");
            var type = location.Kind == LocationKind.Area ? "Área WIP" : "Posición WIP";
            var valid = stage.IsActive && location.OperationalRole == LocationOperationalRole.Wip && location.IsOperational && Associated(location, associations);
            return new(key, location.Code, type, location.Description ?? type, valid,
                valid ? null : !stage.IsActive ? "El proceso está inactivo." : !location.IsOperational ? "La ubicación está inactiva, bloqueada o retirada." : "El destino ya no es compatible con el proceso.");
        }
        if (parsed.RowCode is not null && parsed.RackNumber is not null)
        {
            var positions = await db.Locations.AsNoTracking().Where(x => x.Kind == LocationKind.Rack &&
                x.RowCode == parsed.RowCode && x.RackNumber == parsed.RackNumber).ToListAsync(token);
            var associated = associations.Any(x => x.RowCode == parsed.RowCode && (x.RackNumber == null || x.RackNumber == parsed.RackNumber));
            var wip = positions.Where(x => x.OperationalRole == LocationOperationalRole.Wip).ToArray();
            var valid = stage.IsActive && associated && wip.Any(x => x.IsOperational);
            var partial = valid && wip.Any(x => !x.IsOperational);
            return new(key, $"{parsed.RowCode}-{parsed.RackNumber}", "Rack WIP", "La posición exacta se confirma al surtir", valid,
                valid ? partial ? "Disponibilidad parcial" : null : "El rack no tiene posiciones WIP disponibles o ya no pertenece al proceso.");
        }
        return new(key ?? "", "Destino inválido", "Destino WIP", "Selecciona un destino válido", false, "Selecciona un área, rack o posición WIP.");
    }

    internal static string Key(Guid? locationId, string? rowCode, short? rackNumber) =>
        locationId is Guid id ? $"P:{id}" : rowCode is not null && rackNumber is not null ? $"R:{rowCode}:{rackNumber}" : "";
    internal static (Guid? LocationId, string? RowCode, short? RackNumber) Parse(string? key)
    {
        if (key?.StartsWith("P:", StringComparison.Ordinal) == true && Guid.TryParse(key[2..], out var id)) return (id, null, null);
        if (key?.Split(':') is ["R", var row, var number] && short.TryParse(number, out var rack)) return (null, row.Trim().ToUpperInvariant(), rack);
        return (null, null, null);
    }
    private static bool Associated(Location location, IReadOnlyCollection<ProductionProcessWipTarget> targets) =>
        location.Kind == LocationKind.Area ? targets.Any(x => x.LocationId == location.Id) :
        targets.Any(x => x.RowCode == location.RowCode && (x.RackNumber == null || x.RackNumber == location.RackNumber));
    private static string NormalizeKey(string key)
    {
        var parsed = Parse(key);
        return Key(parsed.LocationId, parsed.RowCode, parsed.RackNumber);
    }
    private async Task<uint> VersionAsync(CancellationToken token) =>
        (await db.ProductionProcessConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, token))?.Version ?? 0;
    private static WipDefaultResult Invalid(string error) => new(WipDefaultStatus.ValidationFailed, [error]);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
