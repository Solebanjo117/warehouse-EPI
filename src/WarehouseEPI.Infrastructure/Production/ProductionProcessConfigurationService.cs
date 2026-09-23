using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Production;

public enum ProcessConfigurationStatus { Success, InvalidPin, NotFound, ValidationFailed, ConcurrencyConflict, IdempotencyConflict }
public sealed record ProcessConfigurationResult(ProcessConfigurationStatus Status, Guid? ProcessId = null, IReadOnlyList<string>? Errors = null);
public sealed record WipRackKey(string RowCode, short RackNumber);
public sealed record WipTargetOption(string Key, string Label, bool IsActive, bool Selected);
public sealed record WipTargetSuggestion(string Key, string Label, string Type, string Description);
public sealed record ProcessRow(Guid Id, string Code, string Name, bool IsActive, string Targets, string DefaultWip, bool DefaultWipAvailable);
public sealed record ProcessListPage(IReadOnlyList<ProcessRow> Items, int PageNumber, bool HasPrevious, bool HasNext);
public sealed record ProcessEditView(Guid Id, string Code, string Name, bool IsActive, uint Version,
    IReadOnlyList<WipTargetOption> Areas, IReadOnlyList<WipTargetOption> Rows, IReadOnlyList<WipTargetOption> Racks,
    string DefaultWipTargetKey, string DefaultWipTargetLabel, bool DefaultWipTargetAvailable,
    int? InactivityAlertHours, int? ReworkAlertHours);
public sealed record SaveProcessCommand(Guid OperationId, Guid Id, string Code, string Name, bool IsActive, uint ExpectedVersion,
    IReadOnlyList<Guid> AreaIds, IReadOnlyList<string> Rows, IReadOnlyList<WipRackKey> Racks, string? Reason, string Pin,
    string? DefaultWipTargetKey = null, int? InactivityAlertHours = null, int? ReworkAlertHours = null);

public sealed class ProductionProcessConfigurationService(WarehouseDbContext db, UserPinService pins,
    TimeProvider timeProvider, ILogger<ProductionProcessConfigurationService> logger,
    ProductionWipDefaultService? wipDefaults = null)
{
    private static readonly Action<ILogger, Exception?> LogSaveFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(1, nameof(LogSaveFailure)),
        "No fue posible guardar la configuración del proceso.");

    public async Task<ProcessListPage> ListAsync(string? search, int pageNumber, CancellationToken token = default)
    {
        var query = db.ProductionStages.AsNoTracking().Include(x => x.WipTargets).Include(x => x.DefaultWipLocation).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpper();
            query = query.Where(x => x.Code.ToUpper().Contains(term) || x.Name.ToUpper().Contains(term));
        }
        const int pageSize = 25; pageNumber = Math.Max(1, pageNumber);
        var total = await query.CountAsync(token);
        var stages = await query.OrderBy(x => x.Name).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var areas = await db.Locations.AsNoTracking().Where(x => x.Kind == LocationKind.Area)
            .ToDictionaryAsync(x => x.Id, x => x.Code, token);
        var items = new List<ProcessRow>();
        foreach (var stage in stages)
        {
            var key = ProductionWipDefaultService.Key(stage.DefaultWipLocationId, stage.DefaultWipRowCode, stage.DefaultWipRackNumber);
            var choice = wipDefaults is null || string.IsNullOrEmpty(key)
                ? new WipDefaultChoice(key, "Sin predeterminado", "Destino WIP", "", true)
                : await wipDefaults.DescribeAsync(stage.Id, key, token);
            items.Add(new(stage.Id, stage.Code, stage.Name, stage.IsActive,
                string.Join(", ", stage.WipTargets.Select(t => t.LocationId is Guid id && areas.TryGetValue(id, out var code)
                    ? code : t.RowCode is not null && t.RackNumber is null ? $"Fila {t.RowCode}"
                    : t.RowCode is not null ? $"{t.RowCode}-{t.RackNumber}" : null).Where(v => v is not null).OrderBy(v => v)),
                choice.Label, choice.IsAvailable));
        }
        return new(items, pageNumber, pageNumber > 1, pageNumber * pageSize < total);
    }

    public async Task<ProcessEditView?> GetAsync(Guid id, CancellationToken token = default)
    {
        var isNew = id == Guid.Empty;
        var stage = isNew ? new ProductionStage { Code = "", Name = "" }
            : await db.ProductionStages.AsNoTracking().Include(x => x.WipTargets).Include(x => x.DefaultWipLocation).SingleOrDefaultAsync(x => x.Id == id, token);
        if (stage is null) return null;
        var selectedAreas = stage.WipTargets.Where(x => x.LocationId != null).Select(x => x.LocationId!.Value).ToHashSet();
        var selectedRows = stage.WipTargets.Where(x => x.RowCode != null && x.RackNumber == null).Select(x => x.RowCode!).ToHashSet();
        var selectedRacks = stage.WipTargets.Where(x => x.RowCode != null && x.RackNumber != null).Select(x => $"{x.RowCode}|{x.RackNumber}").ToHashSet();
        var areas = await db.Locations.AsNoTracking()
            .Where(x => x.Kind == LocationKind.Area && (x.OperationalRole == LocationOperationalRole.Wip || selectedAreas.Contains(x.Id)))
            .OrderBy(x => x.Code).Select(x => new WipTargetOption($"A:{x.Id}", x.Code,
                x.IsActive && x.IsPhysicallyPresent && x.OperationalRole == LocationOperationalRole.Wip, selectedAreas.Contains(x.Id))).ToListAsync(token);
        var rowStates = await db.Locations.AsNoTracking()
            .Where(x => x.Kind == LocationKind.Rack && x.RowCode != null)
            .GroupBy(x => x.RowCode!)
            .Select(group => new { RowCode = group.Key, Active = group.Any(x => x.IsActive && x.IsPhysicallyPresent) })
            .ToListAsync(token);
        var knownRows = rowStates.Select(x => x.RowCode).ToHashSet();
        var rows = rowStates.Where(x => x.Active || selectedRows.Contains(x.RowCode))
            .Select(x => new WipTargetOption($"F:{x.RowCode}", $"Fila {x.RowCode}", x.Active, selectedRows.Contains(x.RowCode)))
            .Concat(selectedRows.Where(x => !knownRows.Contains(x))
                .Select(x => new WipTargetOption($"F:{x}", $"Fila {x}", false, true)))
            .OrderBy(x => x.Label).ToArray();
        var rackRows = await db.Locations.AsNoTracking().Where(x => x.Kind == LocationKind.Rack && x.RowCode != null && x.RackNumber != null)
            .GroupBy(x => new { x.RowCode, x.RackNumber }).Select(g => new
            {
                g.Key.RowCode,
                g.Key.RackNumber,
                IsWip = g.Any(x => x.OperationalRole == LocationOperationalRole.Wip),
                Active = g.Any(x => x.OperationalRole == LocationOperationalRole.Wip && x.IsActive && x.IsPhysicallyPresent && !x.IsBlocked)
            }).ToListAsync(token);
        var racks = rackRows.Where(x => x.IsWip || selectedRacks.Contains($"{x.RowCode}|{x.RackNumber}"))
            .OrderBy(x => x.RowCode).ThenBy(x => x.RackNumber)
            .Select(x => new WipTargetOption($"R:{x.RowCode}:{x.RackNumber}", $"{x.RowCode}-{x.RackNumber}",
                x.IsWip && x.Active, selectedRacks.Contains($"{x.RowCode}|{x.RackNumber}"))).ToArray();
        var defaultKey = ProductionWipDefaultService.Key(stage.DefaultWipLocationId, stage.DefaultWipRowCode, stage.DefaultWipRackNumber);
        var defaultChoice = wipDefaults is null || string.IsNullOrEmpty(defaultKey)
            ? new WipDefaultChoice(defaultKey, string.IsNullOrEmpty(defaultKey) ? "Sin predeterminado" : defaultKey, "Destino WIP", "", string.IsNullOrEmpty(defaultKey))
            : await wipDefaults.DescribeAsync(stage.Id, defaultKey, token);
        return new(isNew ? Guid.Empty : stage.Id, stage.Code, stage.Name, stage.IsActive,
            await VersionAsync(token), areas, rows, racks, defaultKey, defaultChoice.Label, defaultChoice.IsAvailable,
            stage.InactivityAlertHours, stage.ReworkAlertHours);
    }

    public async Task<IReadOnlyList<WipTargetSuggestion>> SearchWipTargetsAsync(string? search,
        CancellationToken token = default)
    {
        var term = search?.Trim();
        if (string.IsNullOrWhiteSpace(term)) return [];

        var areas = await db.Locations.AsNoTracking()
            .Where(x => x.Kind == LocationKind.Area && x.OperationalRole == LocationOperationalRole.Wip
                && x.IsActive && x.IsPhysicallyPresent)
            .Select(x => new { x.Id, x.Code })
            .ToListAsync(token);
        var rackRows = await db.Locations.AsNoTracking()
            .Where(x => x.Kind == LocationKind.Rack && x.RowCode != null && x.RackNumber != null)
            .GroupBy(x => new { x.RowCode, x.RackNumber })
            .Select(group => new
            {
                group.Key.RowCode,
                group.Key.RackNumber,
                IsWip = group.Any(x => x.OperationalRole == LocationOperationalRole.Wip),
                IsAvailable = group.Any(x => x.OperationalRole == LocationOperationalRole.Wip && x.IsActive && x.IsPhysicallyPresent && !x.IsBlocked)
            })
            .ToListAsync(token);
        var rows = rackRows.Where(x => x.IsAvailable).Select(x => x.RowCode!).Distinct()
            .Select(row => new WipTargetSuggestion($"F:{row}", $"Fila {row}", "row",
                "Aplica a todos los racks de la fila, incluidos los de almacenamiento"));

        return areas
            .Select(x => new WipTargetSuggestion($"A:{x.Id}", x.Code, "area", "Área WIP"))
            .Concat(rows)
            .Concat(rackRows
                .Where(x => x.IsWip && x.IsAvailable)
                .Select(x => new WipTargetSuggestion($"R:{x.RowCode}:{x.RackNumber}",
                    $"{x.RowCode}-{x.RackNumber}", "rack", "Rack con posiciones WIP")))
            .Where(x => x.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
    }

    public async Task<ProcessConfigurationResult> SaveProcessAsync(SaveProcessCommand command, CancellationToken token = default)
    {
        var normalizedRows = command.Rows.Select(LocationNormalization.NormalizeRowCode)
            .Distinct(StringComparer.Ordinal).ToArray();
        var normalizedRacks = command.Racks
            .Select(x => new WipRackKey(LocationNormalization.NormalizeRowCode(x.RowCode), x.RackNumber))
            .Where(x => !normalizedRows.Contains(x.RowCode, StringComparer.Ordinal))
            .Distinct().ToArray();
        command = command with { Rows = normalizedRows, Racks = normalizedRacks };
        var user = await pins.AuthenticateAsync(command.Pin, token);
        if (user?.Role.Code != "ADMIN") return new(ProcessConfigurationStatus.InvalidPin);
        if (command.OperationId == Guid.Empty) return Invalid("La operación no es válida.");
        var code = command.Code.Trim().ToUpperInvariant(); var name = command.Name.Trim();
        if (code.Length is < 1 or > 40 || name.Length is < 1 or > 120)
            return Invalid("Indica un código y un nombre válidos.");
        if (command.InactivityAlertHours is <= 0 or > 8760 || command.ReworkAlertHours is <= 0 or > 8760)
            return Invalid("Los umbrales deben estar entre 1 y 8,760 horas, o quedar vacíos.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        try
        {
            var fingerprint = Hash(JsonSerializer.Serialize(command with { Pin = "" }));
            var prior = await db.ProductionProcessRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == command.OperationId, token);
            if (prior is not null) return await Abort(transaction, prior.RequestFingerprint == fingerprint
                ? new(ProcessConfigurationStatus.Success, prior.ProductionStageId)
                : new(ProcessConfigurationStatus.IdempotencyConflict, Errors: ["Esta operación ya se utilizó con datos distintos."]), token);
            var configuration = await ConfigurationAsync(token);
            if (configuration.Version != command.ExpectedVersion) return await Abort(transaction, new(ProcessConfigurationStatus.ConcurrencyConflict), token);
            var stage = command.Id == Guid.Empty ? new ProductionStage { Code = code, Name = name } :
                await db.ProductionStages.Include(x => x.WipTargets).SingleOrDefaultAsync(x => x.Id == command.Id, token);
            if (stage is null) return await Abort(transaction, new(ProcessConfigurationStatus.NotFound), token);
            if (await db.ProductionStages.AnyAsync(x => x.Code == code && x.Id != stage.Id, token))
                return await Abort(transaction, Invalid("Ya existe un proceso con ese código."), token);
            if (!command.IsActive && await db.ProductionRouteStages.AnyAsync(x => x.StageId == stage.Id && x.Route.IsActive, token))
                return await Abort(transaction, Invalid("No se puede desactivar porque pertenece a una ruta activa."), token);
            var validation = await ValidateTargets(stage.Id, command.AreaIds, command.Rows, command.Racks, token);
            var currentDefault = ProductionWipDefaultService.Key(stage.DefaultWipLocationId, stage.DefaultWipRowCode, stage.DefaultWipRackNumber);
            var requestedDefault = ProductionWipDefaultService.Key(
                ProductionWipDefaultService.Parse(command.DefaultWipTargetKey).LocationId,
                ProductionWipDefaultService.Parse(command.DefaultWipTargetKey).RowCode,
                ProductionWipDefaultService.Parse(command.DefaultWipTargetKey).RackNumber);
            if (!string.IsNullOrWhiteSpace(command.DefaultWipTargetKey) && string.IsNullOrEmpty(requestedDefault))
                validation.Add("Selecciona un WIP predeterminado válido.");
            var defaultChanged = !string.Equals(currentDefault, requestedDefault, StringComparison.Ordinal);
            if (defaultChanged)
                validation.AddRange(await ValidateDefaultTarget(command.DefaultWipTargetKey, command.AreaIds, command.Rows, command.Racks, token));
            if (validation.Count > 0) return await Abort(transaction, new(ProcessConfigurationStatus.ValidationFailed, Errors: validation), token);
            var rowChanged = !stage.WipTargets.Where(x => x.RowCode != null && x.RackNumber == null).Select(x => x.RowCode!)
                .ToHashSet(StringComparer.Ordinal).SetEquals(command.Rows);
            var rackChanged = stage.WipTargets.Where(x => x.RowCode != null && x.RackNumber != null).Select(x => new WipRackKey(x.RowCode!, x.RackNumber!.Value)).ToHashSet()
                .SetEquals(command.Racks.Select(x => new WipRackKey(x.RowCode.Trim().ToUpperInvariant(), x.RackNumber))) == false;
            var alertChanged = stage.InactivityAlertHours != command.InactivityAlertHours
                || stage.ReworkAlertHours != command.ReworkAlertHours;
            if ((rowChanged || rackChanged || defaultChanged || alertChanged) && string.IsNullOrWhiteSpace(command.Reason))
                return await Abort(transaction, Invalid("Indica el motivo del cambio en filas o racks, WIP predeterminado o umbrales de alerta."), token);
            var before = JsonSerializer.Serialize(new
            {
                stage.Code,
                stage.Name,
                stage.IsActive,
                stage.InactivityAlertHours,
                stage.ReworkAlertHours,
                DefaultWipTarget = ProductionWipDefaultService.Key(stage.DefaultWipLocationId, stage.DefaultWipRowCode, stage.DefaultWipRackNumber),
                Areas = stage.WipTargets.Where(x => x.LocationId != null).Select(x => x.LocationId).OrderBy(x => x),
                Rows = stage.WipTargets.Where(x => x.RowCode != null && x.RackNumber == null).Select(x => x.RowCode).OrderBy(x => x),
                Racks = stage.WipTargets.Where(x => x.RowCode != null && x.RackNumber != null).Select(x => new { x.RowCode, x.RackNumber }).OrderBy(x => x.RowCode).ThenBy(x => x.RackNumber)
            });
            if (command.Id == Guid.Empty) db.ProductionStages.Add(stage);
            stage.Code = code; stage.Name = name; stage.IsActive = command.IsActive;
            var defaultTarget = ProductionWipDefaultService.Parse(command.DefaultWipTargetKey);
            stage.DefaultWipLocationId = defaultTarget.LocationId;
            stage.DefaultWipRowCode = defaultTarget.RowCode;
            stage.DefaultWipRackNumber = defaultTarget.RackNumber;
            stage.InactivityAlertHours = command.InactivityAlertHours;
            stage.ReworkAlertHours = command.ReworkAlertHours;
            db.ProductionProcessWipTargets.RemoveRange(stage.WipTargets);
            stage.WipTargets = command.AreaIds.Distinct().Select(id => new ProductionProcessWipTarget { LocationId = id, CreatedAt = timeProvider.GetUtcNow() })
                .Concat(command.Rows.Select(row => new ProductionProcessWipTarget { RowCode = row, CreatedAt = timeProvider.GetUtcNow() }))
                .Concat(command.Racks.Distinct().Select(r => new ProductionProcessWipTarget { RowCode = r.RowCode.Trim().ToUpperInvariant(), RackNumber = r.RackNumber, CreatedAt = timeProvider.GetUtcNow() })).ToList();
            configuration.Version++;
            db.ProductionProcessRevisions.Add(new ProductionProcessRevision
            {
                OperationId = command.OperationId,
                RequestFingerprint = fingerprint,
                ProductionStage = stage,
                AuthorizedByUserId = user.Id,
                Reason = command.Reason?.Trim() ?? "Actualización del proceso",
                BeforeJson = before,
                AfterJson = JsonSerializer.Serialize(new
                {
                    Code = code,
                    Name = name,
                    IsActive = command.IsActive,
                    command.InactivityAlertHours,
                    command.ReworkAlertHours,
                    DefaultWipTarget = ProductionWipDefaultService.Key(defaultTarget.LocationId, defaultTarget.RowCode, defaultTarget.RackNumber),
                    Areas = command.AreaIds.Distinct().OrderBy(x => x),
                    Rows = command.Rows.OrderBy(x => x),
                    Racks = command.Racks.Select(x => new { x.RowCode, x.RackNumber }).Distinct().OrderBy(x => x.RowCode).ThenBy(x => x.RackNumber)
                }),
                RecordedAt = timeProvider.GetUtcNow()
            });
            await db.SaveChangesAsync(token); if (transaction is not null) await transaction.CommitAsync(token);
            return new(ProcessConfigurationStatus.Success, stage.Id);
        }
        catch (DbUpdateConcurrencyException) { if (transaction is not null) await transaction.RollbackAsync(token); return new(ProcessConfigurationStatus.ConcurrencyConflict); }
        catch (DbUpdateException exception)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            LogSaveFailure(logger, exception);
            return Invalid("La configuración no pudo guardarse. Verifica que la actualización de base de datos esté aplicada.");
        }
    }

    public async Task<(uint Version, IReadOnlyList<ProductionStage> Processes)> GetProcessesAsync(IEnumerable<Guid> selected, CancellationToken token = default)
    {
        var ids = selected.ToHashSet();
        var list = await db.ProductionStages.AsNoTracking().Where(x => x.IsActive || ids.Contains(x.Id)).OrderBy(x => x.Name).ToListAsync(token);
        return (await VersionAsync(token), list);
    }

    public async Task<ProcessConfigurationResult> ApplyAreaAsync(Guid locationId, LocationOperationalRole role,
        IReadOnlyList<Guid> processIds, uint expectedVersion, CancellationToken token = default)
    {
        var configuration = await ConfigurationAsync(token);
        if (configuration.Version != expectedVersion) return new(ProcessConfigurationStatus.ConcurrencyConflict);
        var existing = await db.ProductionProcessWipTargets.Where(x => x.LocationId == locationId).ToListAsync(token);
        var requested = role == LocationOperationalRole.Wip ? processIds.Distinct().ToArray() : [];
        var existingIds = existing.Select(x => x.ProductionStageId).ToArray();
        if (await db.ProductionStages.CountAsync(x => requested.Contains(x.Id) && (x.IsActive || existingIds.Contains(x.Id)), token) != requested.Length) return Invalid("Uno de los procesos no está disponible.");
        db.RemoveRange(existing.Where(x => !requested.Contains(x.ProductionStageId)));
        foreach (var id in requested.Except(existing.Select(x => x.ProductionStageId))) db.Add(new ProductionProcessWipTarget { ProductionStageId = id, LocationId = locationId, CreatedAt = timeProvider.GetUtcNow() });
        configuration.Version++; return new(ProcessConfigurationStatus.Success);
    }

    public async Task<ProcessConfigurationResult> ApplyRackAsync(string rowCode, short rackNumber, LocationOperationalRole role,
        IReadOnlyList<Guid> processIds, uint expectedVersion, CancellationToken token = default)
    {
        var configuration = await ConfigurationAsync(token);
        if (configuration.Version != expectedVersion) return new(ProcessConfigurationStatus.ConcurrencyConflict);
        var row = rowCode.Trim().ToUpperInvariant();
        var existing = await db.ProductionProcessWipTargets.Where(x => x.RowCode == row && x.RackNumber == rackNumber).ToListAsync(token);
        var inherited = await db.ProductionProcessWipTargets.Where(x => x.RowCode == row && x.RackNumber == null)
            .Select(x => x.ProductionStageId).ToListAsync(token);
        var requested = role == LocationOperationalRole.Wip ? processIds.Distinct().Except(inherited).ToArray() : [];
        var existingIds = existing.Select(x => x.ProductionStageId).ToArray();
        if (await db.ProductionStages.CountAsync(x => requested.Contains(x.Id) && (x.IsActive || existingIds.Contains(x.Id)), token) != requested.Length) return Invalid("Uno de los procesos no está disponible.");
        db.RemoveRange(existing.Where(x => !requested.Contains(x.ProductionStageId)));
        foreach (var id in requested.Except(existing.Select(x => x.ProductionStageId))) db.Add(new ProductionProcessWipTarget { ProductionStageId = id, RowCode = row, RackNumber = rackNumber, CreatedAt = timeProvider.GetUtcNow() });
        configuration.Version++; return new(ProcessConfigurationStatus.Success);
    }

    public Task<List<Guid>> AreaProcessIdsAsync(Guid id, CancellationToken token = default) => db.ProductionProcessWipTargets.AsNoTracking().Where(x => x.LocationId == id).Select(x => x.ProductionStageId).ToListAsync(token);
    public Task<List<Guid>> RowProcessIdsAsync(string row, CancellationToken token = default)
    {
        var normalized = LocationNormalization.NormalizeRowCode(row);
        return db.ProductionProcessWipTargets.AsNoTracking()
            .Where(x => x.RowCode == normalized && x.RackNumber == null)
            .Select(x => x.ProductionStageId).ToListAsync(token);
    }
    public Task<List<Guid>> RackDirectProcessIdsAsync(string row, short rack, CancellationToken token = default)
    {
        var normalized = LocationNormalization.NormalizeRowCode(row);
        return db.ProductionProcessWipTargets.AsNoTracking()
            .Where(x => x.RowCode == normalized && x.RackNumber == rack)
            .Select(x => x.ProductionStageId).ToListAsync(token);
    }
    public Task<List<Guid>> RackProcessIdsAsync(string row, short rack, CancellationToken token = default)
    {
        var normalized = LocationNormalization.NormalizeRowCode(row);
        return db.ProductionProcessWipTargets.AsNoTracking()
            .Where(x => x.RowCode == normalized && (x.RackNumber == null || x.RackNumber == rack))
            .Select(x => x.ProductionStageId).Distinct().ToListAsync(token);
    }
    public Task<uint> CurrentVersionAsync(CancellationToken token = default) => VersionAsync(token);

    private async Task<List<string>> ValidateTargets(Guid stageId, IReadOnlyList<Guid> areaIds, IReadOnlyList<string> rows,
        IReadOnlyList<WipRackKey> racks, CancellationToken token)
    {
        var errors = new List<string>(); var distinctAreas = areaIds.Distinct().ToArray();
        var existingAreas = await db.ProductionProcessWipTargets.Where(x => x.ProductionStageId == stageId && x.LocationId != null).Select(x => x.LocationId!.Value).ToListAsync(token);
        if (await db.Locations.CountAsync(x => distinctAreas.Contains(x.Id) && x.Kind == LocationKind.Area && x.OperationalRole == LocationOperationalRole.Wip && ((x.IsActive && x.IsPhysicallyPresent) || existingAreas.Contains(x.Id)), token) != distinctAreas.Length)
            errors.Add("Una de las áreas seleccionadas no es un área WIP activa.");
        var existingRows = await db.ProductionProcessWipTargets
            .Where(x => x.ProductionStageId == stageId && x.RowCode != null && x.RackNumber == null)
            .Select(x => x.RowCode!).ToListAsync(token);
        foreach (var row in rows)
        {
            if (!LocationNormalization.IsValidRowCode(row)
                || (!existingRows.Contains(row) && !await db.Locations.AnyAsync(x => x.Kind == LocationKind.Rack
                    && x.RowCode == row && x.IsActive && x.IsPhysicallyPresent, token)))
                errors.Add($"La fila {row} no está disponible.");
        }
        foreach (var rack in racks.Distinct())
        {
            var row = rack.RowCode.Trim().ToUpperInvariant();
            var states = await db.Locations.Where(x => x.Kind == LocationKind.Rack && x.RowCode == row && x.RackNumber == rack.RackNumber).Select(x => new { x.OperationalRole, x.IsActive, x.IsPhysicallyPresent }).ToListAsync(token);
            var existed = await db.ProductionProcessWipTargets.AnyAsync(x => x.ProductionStageId == stageId && x.RowCode == row && x.RackNumber == rack.RackNumber, token);
            if (states.Count == 0 || (!existed && !states.Any(x => x.OperationalRole == LocationOperationalRole.Wip && x.IsActive && x.IsPhysicallyPresent))) errors.Add($"El rack {row}-{rack.RackNumber} no tiene posiciones WIP disponibles.");
        }
        return errors;
    }

    private async Task<ProductionProcessConfiguration> ConfigurationAsync(CancellationToken token)
    {
        var value = await db.ProductionProcessConfigurations.SingleOrDefaultAsync(x => x.Id == 1, token);
        if (value is not null) return value;
        value = new ProductionProcessConfiguration(); db.Add(value); return value;
    }
    private async Task<uint> VersionAsync(CancellationToken token) => (await db.ProductionProcessConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, token))?.Version ?? 0;
    private async Task<List<string>> ValidateDefaultTarget(string? key, IReadOnlyList<Guid> areas, IReadOnlyList<string> rows,
        IReadOnlyList<WipRackKey> racks, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key)) return [];
        var parsed = ProductionWipDefaultService.Parse(key);
        if (parsed.LocationId is Guid locationId)
        {
            var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == locationId, token);
            if (location is null || !location.IsOperational || location.OperationalRole != LocationOperationalRole.Wip)
                return ["El WIP predeterminado no está disponible."];
            var associated = location.Kind == LocationKind.Area ? areas.Contains(location.Id) :
                rows.Contains(location.RowCode ?? "", StringComparer.Ordinal) ||
                racks.Contains(new WipRackKey(location.RowCode ?? "", location.RackNumber ?? 0));
            return associated ? [] : ["El WIP predeterminado debe pertenecer a las asociaciones del proceso."];
        }
        if (parsed.RowCode is null || parsed.RackNumber is null) return ["Selecciona un WIP predeterminado válido."];
        var permitted = rows.Contains(parsed.RowCode, StringComparer.Ordinal) ||
            racks.Contains(new WipRackKey(parsed.RowCode, parsed.RackNumber.Value));
        var positions = await db.Locations.AsNoTracking().Where(x => x.Kind == LocationKind.Rack &&
            x.RowCode == parsed.RowCode && x.RackNumber == parsed.RackNumber).ToListAsync(token);
        return permitted && positions.Any(x => x.OperationalRole == LocationOperationalRole.Wip && x.IsOperational)
            ? [] : ["El rack predeterminado debe tener una posición WIP disponible y pertenecer al proceso."];
    }
    private static ProcessConfigurationResult Invalid(string error) => new(ProcessConfigurationStatus.ValidationFailed, Errors: [error]);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task<ProcessConfigurationResult> Abort(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx, ProcessConfigurationResult result, CancellationToken token) { if (tx is not null) await tx.RollbackAsync(token); return result; }
}
