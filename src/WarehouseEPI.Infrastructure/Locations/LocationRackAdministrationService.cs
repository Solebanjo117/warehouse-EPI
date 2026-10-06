using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WarehouseEPI.Core;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Locations;

public sealed record LocationRackEditCommand(Guid OperationId, Guid RequestedByUserId, string RowCode,
    short RackNumber, LocationOperationalRole OperationalRole,
    IReadOnlyCollection<short> PresentPallets, string? Reason, string? Pin,
    IReadOnlyList<Guid>? ProcessIds = null, uint? ProcessConfigurationVersion = null,
    IReadOnlyCollection<short>? WipPallets = null, RackFormat? Format = null,
    RackWipAssociationChange? WipAssociation = null);

public sealed record RackWipAssociationChange(Guid? WipAreaId, Guid? ExpectedWipAreaId);

public sealed record LocationRackPositionState(Guid? Id, short PalletNumber, string Code,
    bool Exists, bool IsPhysicallyPresent, bool IsActive, bool IsBlocked, bool HasBalance,
    bool HasActiveAssignments, LocationOperationalRole OperationalRole);

public sealed record LocationRackEditView(string RowCode, short RackNumber,
    LocationOperationalRole OperationalRole, IReadOnlyList<LocationRackPositionState> Positions,
    IReadOnlyList<LocationRackRevisionView> Revisions, LocationRackDeletionState Deletion,
    IReadOnlyList<Guid> ProcessIds, IReadOnlyList<Guid> InheritedProcessIds, uint ProcessConfigurationVersion)
{
    public RackFormat Format { get; init; } = RackFormat.Default;
    public RackWipAssociationView? WipAssociation { get; init; }
}

public sealed record LocationRackDeletionState(bool CanDelete, IReadOnlyList<string> Blockers);

public sealed record LocationRackDeleteCommand(Guid OperationId, Guid RequestedByUserId, string RowCode,
    short RackNumber, string? Reason, string? Pin, string? ConfirmationCode);

public sealed record LocationRackRevisionView(Guid Id, string Reason, string RequestedBy,
    string AuthorizedBy, DateTimeOffset RecordedAt, string BeforeJson, string AfterJson);

public sealed record LocationRackEditSummary(IReadOnlyList<string> Added, IReadOnlyList<string> Restored,
    IReadOnlyList<string> Retired, LocationOperationalRole PreviousOperationalRole,
    LocationOperationalRole RequestedOperationalRole,
    IReadOnlyList<string>? RoleChanges = null, bool DirectProcessesRemoved = false)
{
    public Guid? PreviousWipAreaId { get; init; }
    public Guid? RequestedWipAreaId { get; init; }
    public bool OperationalRoleChanged => PreviousOperationalRole != RequestedOperationalRole || RoleChanges is { Count: > 0 };
}

public sealed record LocationRackReviewResult(IReadOnlyList<string> Errors, LocationRackEditSummary Summary);

public enum LocationRackSaveStatus { Success, ValidationFailed, Unauthorized, InvalidPin, IdempotencyConflict, NotFound }

public sealed record LocationRackSaveResult(LocationRackSaveStatus Status,
    IReadOnlyList<string>? Errors = null);

public enum LocationRackDeleteStatus { Success, ValidationFailed, Unauthorized, InvalidPin, IdempotencyConflict, NotFound }

public sealed record LocationRackDeleteResult(LocationRackDeleteStatus Status,
    IReadOnlyList<string>? Errors = null);

public sealed class LocationRackAdministrationService(
    WarehouseDbContext dbContext,
    UserPinService pins,
    TimeProvider timeProvider)
{
    public async Task<LocationRackEditView?> GetAsync(string? rowCode, short rackNumber,
        CancellationToken token = default)
    {
        var row = LocationNormalization.NormalizeRowCode(rowCode);
        var locations = await LoadRackAsync(row, rackNumber, false, token);
        if (locations.Count == 0) return null;
        var states = await BuildStatesAsync(row, rackNumber, locations, token);
        var revisions = await dbContext.LocationRackRevisions.AsNoTracking()
            .Where(item => item.RowCode == row && item.RackNumber == rackNumber)
            .OrderByDescending(item => item.RecordedAt)
            .Take(20)
            .Select(item => new LocationRackRevisionView(item.Id, item.Reason,
                item.RequestedByUser.FullName, item.AuthorizedByUser.FullName, item.RecordedAt,
                item.BeforeJson, item.AfterJson))
            .ToListAsync(token);
        var deletion = await GetDeletionStateAsync(locations, token);
        var processIds = await dbContext.ProductionProcessWipTargets.AsNoTracking()
            .Where(x => x.RowCode == row && x.RackNumber == rackNumber).Select(x => x.ProductionStageId).ToListAsync(token);
        var inheritedProcessIds = await dbContext.ProductionProcessWipTargets.AsNoTracking()
            .Where(x => x.RowCode == row && x.RackNumber == null).Select(x => x.ProductionStageId).ToListAsync(token);
        var processVersion = (await dbContext.ProductionProcessConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, token))?.Version ?? 0;
        return new(row, rackNumber, RackOperationalRole(locations), states, revisions, deletion, processIds, inheritedProcessIds, processVersion)
        {
            Format = await LocationRackFormats.GetAsync(dbContext, row, rackNumber, token),
            WipAssociation = await LocationRackWipAssociations.GetAsync(dbContext, row, rackNumber, token)
        };
    }

    public async Task<LocationRackReviewResult> ReviewAsync(LocationRackEditCommand command,
        CancellationToken token = default)
    {
        var errors = ValidateCommand(command);
        if (errors.Count != 0) return new(errors, EmptySummary());
        var requester = await LoadAdminAsync(command.RequestedByUserId, token);
        if (requester is null) errors.Add("La sesión ADMIN ya no es válida.");
        var row = LocationNormalization.NormalizeRowCode(command.RowCode);
        var locations = await LoadRackAsync(row, command.RackNumber, true, token);
        if (locations.Count == 0) errors.Add("El rack no existe.");
        var desired = command.PresentPallets.ToHashSet();
        var format = command.Format ?? await LocationRackFormats.GetAsync(dbContext, row, command.RackNumber, token);
        if (desired.Any(pallet => pallet > format.Capacity))
            errors.Add("Las posiciones seleccionadas deben quedar dentro del formato del rack.");
        errors.AddRange(await ValidateRetirementsAsync(locations, desired, token));
        var summary = BuildSummary(row, command.RackNumber, locations, desired, command);
        var association = await dbContext.LocationRackWipAssociations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RowCode == row && x.RackNumber == command.RackNumber, token);
        errors.AddRange(await ValidateWipAssociationAsync(command, association, token));
        summary = summary with
        {
            PreviousWipAreaId = association?.WipAreaId,
            RequestedWipAreaId = EffectiveWipArea(command, association)
        };
        return new(errors.Distinct(StringComparer.Ordinal).ToArray(), summary);
    }

    public async Task<LocationRackSaveResult> SaveAsync(LocationRackEditCommand command,
        CancellationToken token = default)
    {
        try { return await SaveCoreAsync(command, token); }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            return new(LocationRackSaveStatus.ValidationFailed,
                ["El rack cambió al mismo tiempo. Revisa nuevamente antes de guardar."]);
        }
    }

    private async Task<LocationRackSaveResult> SaveCoreAsync(LocationRackEditCommand command, CancellationToken token)
    {
        var errors = ValidateCommand(command);
        if (errors.Count != 0) return new(LocationRackSaveStatus.ValidationFailed, errors);
        var requester = await LoadAdminAsync(command.RequestedByUserId, token);
        if (requester is null) return new(LocationRackSaveStatus.Unauthorized);
        var authorized = await pins.AuthenticateAsync(command.Pin ?? string.Empty, token);
        if (authorized is null || authorized.Role.Code != "ADMIN") return new(LocationRackSaveStatus.InvalidPin);

        var row = LocationNormalization.NormalizeRowCode(command.RowCode);
        var reason = command.Reason!.Trim();
        var desired = command.PresentPallets.OrderBy(value => value).ToArray();
        var wipPallets = RequestedWipPallets(command);
        var requestContent = JsonSerializer.Serialize(new
        {
            command.RequestedByUserId,
            AuthorizedByUserId = authorized.Id,
            RowCode = row,
            command.RackNumber,
            command.OperationalRole,
            PresentPallets = desired,
            WipPallets = wipPallets.OrderBy(x => x),
            ProcessIds = command.ProcessIds?.OrderBy(x => x),
            command.ProcessConfigurationVersion,
            Reason = reason
        });
        // Keep fingerprints from older requests that did not include a format replayable.
        var fingerprintContent = command.Format is null ? requestContent
            : JsonSerializer.Serialize(new { Content = requestContent, command.Format });
        var fingerprint = Hash(command.WipAssociation is null ? fingerprintContent
            : JsonSerializer.Serialize(new { Content = fingerprintContent, command.WipAssociation }));

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, token)
            : null;
        if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await dbContext.Locations.FromSqlInterpolated(
                $"SELECT * FROM locations WHERE row_code = {row} AND rack_number = {command.RackNumber} FOR UPDATE")
                .LoadAsync(token);

        var existingRevision = await dbContext.LocationRackRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == command.OperationId, token);
        if (existingRevision is not null)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(existingRevision.RequestFingerprint == fingerprint
                ? LocationRackSaveStatus.Success
                : LocationRackSaveStatus.IdempotencyConflict);
        }

        var locations = await LoadRackAsync(row, command.RackNumber, true, token);
        if (locations.Count == 0)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationRackSaveStatus.NotFound);
        }
        var previousFormat = await LocationRackFormats.GetAsync(dbContext, row, command.RackNumber, token);
        var association = await dbContext.LocationRackWipAssociations
            .SingleOrDefaultAsync(x => x.RowCode == row && x.RackNumber == command.RackNumber, token);
        var previousWipAreaId = association?.WipAreaId;
        var requestedWipAreaId = EffectiveWipArea(command, association);
        var requestedFormat = command.Format ?? previousFormat;
        errors = await ValidateRetirementsAsync(locations, desired.ToHashSet(), token);
        errors.AddRange(await ValidateWipAssociationAsync(command, association, token));
        foreach (var location in locations.Where(x => x.PalletNumber.HasValue && desired.Contains(x.PalletNumber.Value)))
        {
            var roleError = await LocationWipRules.ValidateChangeAsync(dbContext, location.Id, location.OperationalRole,
                wipPallets.Contains(location.PalletNumber!.Value) ? LocationOperationalRole.Wip : LocationOperationalRole.Storage, token);
            if (roleError is not null) errors.Add(location.Code + ": " + roleError);
        }
        if (desired.Any(pallet => pallet > requestedFormat.Capacity))
            errors.Add("Las posiciones seleccionadas deben quedar dentro del formato del rack.");
        if (errors.Count != 0)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationRackSaveStatus.ValidationFailed, errors);
        }

        var previousProcessIds = await dbContext.ProductionProcessWipTargets
            .Where(x => x.RowCode == row && x.RackNumber == command.RackNumber)
            .Select(x => x.ProductionStageId).OrderBy(x => x).ToListAsync(token);
        var inheritedProcessIds = await dbContext.ProductionProcessWipTargets
            .Where(x => x.RowCode == row && x.RackNumber == null)
            .Select(x => x.ProductionStageId).OrderBy(x => x).ToListAsync(token);
        IReadOnlyList<Guid> requestedProcessIds = previousProcessIds;
        if (command.ProcessIds is not null)
        {
            var configuration = await dbContext.ProductionProcessConfigurations.SingleOrDefaultAsync(x => x.Id == 1, token);
            var version = configuration?.Version ?? 0;
            if (version != command.ProcessConfigurationVersion)
            {
                if (transaction is not null) await transaction.RollbackAsync(token);
                return new(LocationRackSaveStatus.ValidationFailed, ["La configuración de procesos cambió mientras editabas. Recarga y vuelve a revisar."]);
            }
            configuration ??= new ProductionProcessConfiguration();
            if (dbContext.Entry(configuration).State == EntityState.Detached) dbContext.Add(configuration);
            var requested = wipPallets.Count > 0
                ? command.ProcessIds.Distinct().Except(inheritedProcessIds).ToArray() : [];
            requestedProcessIds = requested.OrderBy(x => x).ToArray();
            var targets = await dbContext.ProductionProcessWipTargets.Where(x => x.RowCode == row && x.RackNumber == command.RackNumber).ToListAsync(token);
            var existingIds = targets.Select(x => x.ProductionStageId).ToArray();
            if (await dbContext.ProductionStages.CountAsync(x => requested.Contains(x.Id) && (x.IsActive || existingIds.Contains(x.Id)), token) != requested.Length)
            {
                if (transaction is not null) await transaction.RollbackAsync(token);
                return new(LocationRackSaveStatus.ValidationFailed, ["Uno de los procesos seleccionados no está disponible."]);
            }
            dbContext.RemoveRange(targets.Where(x => !requested.Contains(x.ProductionStageId)));
            foreach (var id in requested.Except(targets.Select(x => x.ProductionStageId))) dbContext.Add(new ProductionProcessWipTarget { ProductionStageId = id, RowCode = row, RackNumber = command.RackNumber, CreatedAt = timeProvider.GetUtcNow() });
            configuration.Version++;
        }

        var before = JsonSerializer.Serialize(new
        {
            Format = previousFormat,
            Locations = JsonSerializer.Deserialize<JsonElement>(SerializeState(locations)),
            DirectProcessIds = previousProcessIds,
            InheritedRowProcessIds = inheritedProcessIds,
            WipAreaId = previousWipAreaId
        });
        var byPallet = locations.ToDictionary(item => item.PalletNumber!.Value);
        var now = timeProvider.GetUtcNow();
        foreach (var pallet in Enumerable.Range(1, 9).Select(value => (short)value))
        {
            var shouldExist = desired.Contains(pallet);
            if (!byPallet.TryGetValue(pallet, out var location))
            {
                if (!shouldExist) continue;
                location = new Location
                {
                    Code = LocationNormalization.BuildRackCode(row, command.RackNumber, pallet),
                    Kind = LocationKind.Rack,
                    RowCode = row,
                    RackNumber = command.RackNumber,
                    PalletNumber = pallet,
                    OperationalRole = wipPallets.Contains(pallet) ? LocationOperationalRole.Wip : LocationOperationalRole.Storage,
                    IsPhysicallyPresent = true,
                    IsActive = true,
                    UpdatedAt = now
                };
                dbContext.Locations.Add(location);
                locations.Add(location);
            }
            else if (shouldExist && !location.IsPhysicallyPresent)
            {
                location.IsPhysicallyPresent = true;
                location.IsActive = true;
                location.UpdatedAt = now;
            }
            else if (!shouldExist && location.IsPhysicallyPresent)
            {
                location.IsPhysicallyPresent = false;
                location.IsActive = false;
                location.IsBlocked = false;
                location.BlockReason = null;
                location.UpdatedAt = now;
            }

            if (shouldExist && location.OperationalRole != (wipPallets.Contains(pallet) ? LocationOperationalRole.Wip : LocationOperationalRole.Storage))
            {
                location.OperationalRole = wipPallets.Contains(pallet) ? LocationOperationalRole.Wip : LocationOperationalRole.Storage;
                location.UpdatedAt = now;
            }
        }

        var storedFormat = await dbContext.LocationRackFormats.SingleOrDefaultAsync(
            item => item.RowCode == row && item.RackNumber == command.RackNumber, token);
        if (storedFormat is null)
        {
            storedFormat = new LocationRackFormat { RowCode = row, RackNumber = command.RackNumber };
            dbContext.LocationRackFormats.Add(storedFormat);
        }
        storedFormat.Columns = requestedFormat.Columns;
        storedFormat.Levels = requestedFormat.Levels;

        if (requestedWipAreaId is Guid areaId)
        {
            if (association is null)
                dbContext.LocationRackWipAssociations.Add(new LocationRackWipAssociation
                { RowCode = row, RackNumber = command.RackNumber, WipAreaId = areaId });
            else association.WipAreaId = areaId;
        }
        else if (association is not null) dbContext.LocationRackWipAssociations.Remove(association);

        var after = JsonSerializer.Serialize(new
        {
            Format = requestedFormat,
            Locations = JsonSerializer.Deserialize<JsonElement>(SerializeState(locations.OrderBy(item => item.PalletNumber).ToArray())),
            DirectProcessIds = requestedProcessIds,
            InheritedRowProcessIds = inheritedProcessIds,
            WipAreaId = requestedWipAreaId
        });
        dbContext.LocationRackRevisions.Add(new LocationRackRevision
        {
            OperationId = command.OperationId,
            RequestFingerprint = fingerprint,
            RowCode = row,
            RackNumber = command.RackNumber,
            Reason = reason,
            BeforeJson = before,
            AfterJson = after,
            RequestedByUserId = requester.Id,
            AuthorizedByUserId = authorized.Id,
            RecordedAt = now
        });
        try
        {
            await dbContext.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationRackSaveStatus.ValidationFailed,
                ["El rack cambió al mismo tiempo. Revisa nuevamente antes de guardar."]);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationRackSaveStatus.ValidationFailed,
                ["El rack cambió al mismo tiempo. Revisa nuevamente antes de guardar."]);
        }
        return new(LocationRackSaveStatus.Success);
    }

    public async Task<LocationRackDeleteResult> DeleteAsync(LocationRackDeleteCommand command,
        CancellationToken token = default)
    {
        var errors = ValidateDeleteCommand(command);
        if (errors.Count != 0) return new(LocationRackDeleteStatus.ValidationFailed, errors);
        var requester = await LoadAdminAsync(command.RequestedByUserId, token);
        if (requester is null) return new(LocationRackDeleteStatus.Unauthorized);
        var authorized = await pins.AuthenticateAsync(command.Pin ?? string.Empty, token);
        if (authorized is null || authorized.Role.Code != "ADMIN") return new(LocationRackDeleteStatus.InvalidPin);

        var row = LocationNormalization.NormalizeRowCode(command.RowCode);
        var reason = command.Reason!.Trim();
        var rackCode = $"{row}-{command.RackNumber}";
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            Action = "DELETE_RACK",
            command.RequestedByUserId,
            AuthorizedByUserId = authorized.Id,
            RowCode = row,
            command.RackNumber,
            Reason = reason,
            ConfirmationCode = rackCode
        }));

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, token)
            : null;
        if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            await dbContext.Locations.FromSqlInterpolated(
                $"SELECT * FROM locations WHERE row_code = {row} AND rack_number = {command.RackNumber} FOR UPDATE")
                .LoadAsync(token);
            await dbContext.WarehouseMapLayouts.FromSqlInterpolated(
                $"SELECT * FROM warehouse_map_layouts WHERE id = {1} FOR UPDATE").LoadAsync(token);
        }

        var existingRevision = await dbContext.LocationRackRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == command.OperationId, token);
        if (existingRevision is not null)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(existingRevision.RequestFingerprint == fingerprint
                ? LocationRackDeleteStatus.Success
                : LocationRackDeleteStatus.IdempotencyConflict);
        }

        var locations = await LoadRackAsync(row, command.RackNumber, true, token);
        if (locations.Count == 0)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationRackDeleteStatus.NotFound);
        }
        var deletion = await GetDeletionStateAsync(locations, token);
        if (!deletion.CanDelete)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationRackDeleteStatus.ValidationFailed, deletion.Blockers);
        }

        var now = timeProvider.GetUtcNow();
        var mapElements = await dbContext.WarehouseMapElements
            .Where(item => item.Kind == WarehouseMapElementKind.Rack && item.RowCode == row &&
                item.RackNumber == command.RackNumber).ToListAsync(token);
        var association = await dbContext.LocationRackWipAssociations
            .SingleOrDefaultAsync(x => x.RowCode == row && x.RackNumber == command.RackNumber, token);
        var before = JsonSerializer.Serialize(new
        {
            Locations = JsonSerializer.Deserialize<JsonElement>(SerializeState(locations)),
            WipAreaId = association?.WipAreaId
        });
        if (association is not null) dbContext.LocationRackWipAssociations.Remove(association);
        dbContext.LocationRackRevisions.Add(new LocationRackRevision
        {
            OperationId = command.OperationId,
            RequestFingerprint = fingerprint,
            RowCode = row,
            RackNumber = command.RackNumber,
            Reason = reason,
            BeforeJson = before,
            AfterJson = JsonSerializer.Serialize(new
            {
                Deleted = true,
                RackCode = rackCode,
                RemovedLocationIds = locations.Select(item => item.Id),
                RemovedMapElementIds = mapElements.Select(item => item.Id)
            }),
            RequestedByUserId = requester.Id,
            AuthorizedByUserId = authorized.Id,
            RecordedAt = now
        });

        if (mapElements.Count != 0)
        {
            var layout = await dbContext.WarehouseMapLayouts.SingleOrDefaultAsync(item => item.Id == 1, token);
            if (layout is not null)
            {
                var previousVersion = layout.Version;
                layout.Version++;
                layout.UpdatedAt = now;
                layout.UpdatedByUserId = authorized.Id;
                dbContext.WarehouseMapRevisions.Add(new WarehouseMapRevision
                {
                    OperationId = command.OperationId,
                    RequestFingerprint = fingerprint,
                    PreviousVersion = previousVersion,
                    NewVersion = layout.Version,
                    Reason = reason,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        SchemaVersion = 6,
                        Action = "DELETE_RACK",
                        RackCode = rackCode,
                        Removed = mapElements.Select(item => new
                        {
                            item.Id,
                            item.RowCode,
                            item.RackNumber,
                            item.X,
                            item.Y,
                            item.Width,
                            item.Height,
                            item.Rotation,
                            item.ZIndex,
                            item.IsVisible
                        })
                    }),
                    RequestedByUserId = requester.Id,
                    AuthorizedByUserId = authorized.Id,
                    RecordedAt = now
                });
            }
            dbContext.WarehouseMapElements.RemoveRange(mapElements);
        }
        var processTargets = await dbContext.ProductionProcessWipTargets.Where(x => x.RowCode == row && x.RackNumber == command.RackNumber).ToListAsync(token);
        if (processTargets.Count != 0)
        {
            dbContext.RemoveRange(processTargets);
            var configuration = await dbContext.ProductionProcessConfigurations.SingleOrDefaultAsync(x => x.Id == 1, token);
            if (configuration is not null) configuration.Version++;
        }
        var rackFormat = await dbContext.LocationRackFormats.SingleOrDefaultAsync(
            item => item.RowCode == row && item.RackNumber == command.RackNumber, token);
        if (rackFormat is not null) dbContext.LocationRackFormats.Remove(rackFormat);
        dbContext.Locations.RemoveRange(locations);

        try
        {
            await dbContext.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationRackDeleteStatus.ValidationFailed,
                ["El rack cambió al mismo tiempo. Revisa nuevamente antes de eliminar."]);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationRackDeleteStatus.ValidationFailed,
                ["El rack recibió un registro relacionado y ya no puede eliminarse."]);
        }
        return new(LocationRackDeleteStatus.Success);
    }

    private static Guid? EffectiveWipArea(LocationRackEditCommand command, LocationRackWipAssociation? current) =>
        RequestedWipPallets(command).Count == 0 ? null :
        command.WipAssociation is null ? current?.WipAreaId : command.WipAssociation.WipAreaId;

    private static bool IsSerializationFailure(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } ||
        exception.InnerException is not null && IsSerializationFailure(exception.InnerException);

    private async Task<List<string>> ValidateWipAssociationAsync(LocationRackEditCommand command,
        LocationRackWipAssociation? current, CancellationToken token)
    {
        var errors = new List<string>();
        if (command.WipAssociation is { } change && change.ExpectedWipAreaId != current?.WipAreaId)
            errors.Add("La asociación WIP cambió mientras editabas. Recarga y vuelve a revisar.");
        var requested = EffectiveWipArea(command, current);
        if (RequestedWipPallets(command).Count == 0 && command.WipAssociation?.WipAreaId is not null && current is null)
            errors.Add("Selecciona al menos una posición WIP presente para asociar el rack.");
        if (requested is Guid id)
        {
            if (dbContext.Database.CurrentTransaction is not null && dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                await dbContext.Locations.FromSqlInterpolated($"SELECT * FROM locations WHERE id = {id} FOR UPDATE")
                    .AsNoTracking().ToListAsync(token);
            var area = await dbContext.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            // Keep unavailable existing associations visible; never replace their target implicitly.
            if (area is null || area.Kind != LocationKind.Area || !area.IsWip ||
                (current?.WipAreaId != id && !area.IsOperational))
                errors.Add("Selecciona un área WIP activa, presente y sin bloqueo.");
        }
        return errors;
    }

    private async Task<List<Location>> LoadRackAsync(string row, short rack, bool tracking,
        CancellationToken token)
    {
        var query = dbContext.Locations.Where(item => item.Kind == LocationKind.Rack &&
            item.RowCode == row && item.RackNumber == rack);
        if (!tracking) query = query.AsNoTracking();
        return await query.OrderBy(item => item.PalletNumber).ToListAsync(token);
    }

    private async Task<IReadOnlyList<LocationRackPositionState>> BuildStatesAsync(string row, short rack,
        IReadOnlyList<Location> locations, CancellationToken token)
    {
        var ids = locations.Select(item => item.Id).ToArray();
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(item => ids.Contains(item.LocationId))
            .Select(item => new { item.LocationId, item.ProductId, item.Quantity })
            .ToListAsync(token);
        var withBalance = balances.GroupBy(item => new { item.LocationId, item.ProductId })
            .Where(group => group.Sum(item => item.Quantity) != 0)
            .Select(group => group.Key.LocationId).ToHashSet();
        var withAssignments = (await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(item => ids.Contains(item.LocationId) && item.IsActive)
            .Select(item => item.LocationId).Distinct().ToListAsync(token)).ToHashSet();
        var byPallet = locations.ToDictionary(item => item.PalletNumber!.Value);
        return Enumerable.Range(1, 9).Select(number =>
        {
            var pallet = (short)number;
            var exists = byPallet.TryGetValue(pallet, out var location);
            return new LocationRackPositionState(location?.Id, pallet,
                location?.Code ?? LocationNormalization.BuildRackCode(row, rack, pallet), exists,
                location?.IsPhysicallyPresent ?? false, location?.IsActive ?? false,
                location?.IsBlocked ?? false, location is not null && withBalance.Contains(location.Id),
                location is not null && withAssignments.Contains(location.Id),
                location?.OperationalRole ?? LocationOperationalRole.Storage);
        }).ToArray();
    }

    private async Task<List<string>> ValidateRetirementsAsync(IReadOnlyList<Location> locations,
        IReadOnlySet<short> desired, CancellationToken token)
    {
        var retiring = locations.Where(item => item.IsPhysicallyPresent &&
            !desired.Contains(item.PalletNumber!.Value)).ToArray();
        if (retiring.Length == 0) return [];
        var ids = retiring.Select(item => item.Id).ToArray();
        var balances = await dbContext.InventoryBalances.AsNoTracking()
            .Where(item => ids.Contains(item.LocationId))
            .Select(item => new { item.LocationId, item.ProductId, item.Quantity }).ToListAsync(token);
        var balanceIds = balances.GroupBy(item => new { item.LocationId, item.ProductId })
            .Where(group => group.Sum(item => item.Quantity) != 0)
            .Select(group => group.Key.LocationId).ToHashSet();
        var assignmentIds = (await dbContext.ProductLocationAssignments.AsNoTracking()
            .Where(item => ids.Contains(item.LocationId) && item.IsActive)
            .Select(item => item.LocationId).Distinct().ToListAsync(token)).ToHashSet();
        var errors = new List<string>();
        foreach (var item in retiring)
        {
            if (balanceIds.Contains(item.Id)) errors.Add($"{item.Code} conserva saldo y no puede retirarse.");
            if (assignmentIds.Contains(item.Id)) errors.Add($"{item.Code} conserva asignaciones activas y no puede retirarse.");
        }
        return errors;
    }

    private async Task<LocationRackDeletionState> GetDeletionStateAsync(IReadOnlyList<Location> locations,
        CancellationToken token)
    {
        var ids = locations.Select(item => item.Id).ToArray();
        var blockers = new List<string>();
        if (await dbContext.ProductLocationAssignments.AsNoTracking().AnyAsync(item => ids.Contains(item.LocationId), token))
            blockers.Add("Tiene productos asignados, incluso si la asignación ya está inactiva.");
        if (await dbContext.InventoryBalances.AsNoTracking().AnyAsync(item => ids.Contains(item.LocationId), token))
            blockers.Add("Tiene registros de existencias, incluso si el saldo actual es cero.");
        if (await dbContext.InventoryMovementLines.AsNoTracking().AnyAsync(item =>
                (item.SourceLocationId.HasValue && ids.Contains(item.SourceLocationId.Value)) ||
                (item.DestinationLocationId.HasValue && ids.Contains(item.DestinationLocationId.Value)), token) ||
            await dbContext.InventoryMovements.AsNoTracking().AnyAsync(item =>
                item.OperationalAreaId.HasValue && ids.Contains(item.OperationalAreaId.Value), token))
            blockers.Add("Tiene movimientos de inventario o actividad WIP.");
        if (await dbContext.InventoryBalanceChanges.AsNoTracking().AnyAsync(item => ids.Contains(item.LocationId), token))
            blockers.Add("Tiene historial de cambios de saldo.");
        if (await dbContext.CycleCountLocations.AsNoTracking().AnyAsync(item => ids.Contains(item.LocationId), token))
            blockers.Add("Fue incluido en uno o más conteos cíclicos.");
        if (await dbContext.OperationalExceptionCases.AsNoTracking().AnyAsync(item =>
                item.LocationId.HasValue && ids.Contains(item.LocationId.Value), token))
            blockers.Add("Tiene incidencias operativas relacionadas.");
        if (await dbContext.WipDispositions.AsNoTracking().AnyAsync(item =>
                item.DestinationLocationId.HasValue && ids.Contains(item.DestinationLocationId.Value), token))
            blockers.Add("Tiene devoluciones o disposiciones WIP relacionadas.");
        if (await dbContext.WarehouseMapElements.AsNoTracking().AnyAsync(item =>
                item.LocationId.HasValue && ids.Contains(item.LocationId.Value), token))
            blockers.Add("Una posición está ligada directamente a un elemento del croquis.");
        return new(blockers.Count == 0, blockers);
    }

    private async Task<User?> LoadAdminAsync(Guid id, CancellationToken token) =>
        await dbContext.Users.AsNoTracking().Include(item => item.Role)
            .SingleOrDefaultAsync(item => item.Id == id && item.IsActive && item.Role.Code == "ADMIN", token);

    private static List<string> ValidateCommand(LocationRackEditCommand command)
    {
        var errors = new List<string>();
        var row = LocationNormalization.NormalizeRowCode(command.RowCode);
        if (command.OperationId == Guid.Empty) errors.Add("La operación no es válida.");
        if (command.RequestedByUserId == Guid.Empty) errors.Add("La sesión ADMIN no es válida.");
        if (!LocationNormalization.IsValidRowCode(row) || command.RackNumber <= 0)
            errors.Add("La fila o el rack no son válidos.");
        if (command.OperationalRole is not (LocationOperationalRole.Storage or LocationOperationalRole.Wip))
            errors.Add("La función del rack debe ser Almacenamiento o WIP.");
        if (command.PresentPallets.Count is < 1 or > 9 || command.PresentPallets.Distinct().Count() != command.PresentPallets.Count ||
            command.PresentPallets.Any(item => item is < 1 or > 9))
            errors.Add("Selecciona entre una y nueve posiciones distintas.");
        if (command.Format is { } format)
        {
            if (!format.IsValid) errors.Add("Selecciona entre una y tres columnas y entre uno y tres niveles.");
            else if (command.PresentPallets.Any(pallet => pallet > format.Capacity))
                errors.Add("Las posiciones seleccionadas deben quedar dentro del formato del rack.");
        }
        if (command.WipPallets is not null &&
            (command.WipPallets.Distinct().Count() != command.WipPallets.Count ||
             command.WipPallets.Any(item => item is < 1 or > 9 || !command.PresentPallets.Contains(item))))
            errors.Add("Las posiciones WIP deben existir físicamente y no repetirse.");
        var reason = command.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            errors.Add("Escribe un motivo de hasta 500 caracteres.");
        return errors;
    }

    private static List<string> ValidateDeleteCommand(LocationRackDeleteCommand command)
    {
        var errors = new List<string>();
        var row = LocationNormalization.NormalizeRowCode(command.RowCode);
        if (command.OperationId == Guid.Empty) errors.Add("La operación no es válida.");
        if (command.RequestedByUserId == Guid.Empty) errors.Add("La sesión ADMIN no es válida.");
        if (!LocationNormalization.IsValidRowCode(row) || command.RackNumber <= 0)
            errors.Add("La fila o el rack no son válidos.");
        var reason = command.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            errors.Add("Escribe un motivo de hasta 500 caracteres.");
        var expectedCode = $"{row}-{command.RackNumber}";
        if (!string.Equals(command.ConfirmationCode?.Trim(), expectedCode, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Escribe {expectedCode} para confirmar la eliminación definitiva.");
        return errors;
    }

    private static LocationRackEditSummary BuildSummary(string row, short rack,
        IReadOnlyList<Location> locations, IReadOnlySet<short> desired,
        LocationRackEditCommand command)
    {
        var byPallet = locations.ToDictionary(item => item.PalletNumber!.Value);
        var wip = RequestedWipPallets(command);
        var added = desired.Where(item => !byPallet.ContainsKey(item)).Select(item => LocationNormalization.BuildRackCode(row, rack, item)).Order().ToArray();
        var restored = desired.Where(item => byPallet.TryGetValue(item, out var location) && !location.IsPhysicallyPresent).Select(item => byPallet[item].Code).Order().ToArray();
        var retired = locations.Where(item => item.IsPhysicallyPresent && !desired.Contains(item.PalletNumber!.Value)).Select(item => item.Code).Order().ToArray();
        var changes = desired.Where(item => byPallet.TryGetValue(item, out var location) &&
                location.OperationalRole != (wip.Contains(item) ? LocationOperationalRole.Wip : LocationOperationalRole.Storage))
            .Select(item => $"{byPallet[item].Code}: {(byPallet[item].OperationalRole == LocationOperationalRole.Wip ? "WIP" : "Almacenamiento")} → {(wip.Contains(item) ? "WIP" : "Almacenamiento")}")
            .Order().ToArray();
        return new(added, restored, retired, RackOperationalRole(locations),
            wip.Count == desired.Count ? LocationOperationalRole.Wip : LocationOperationalRole.Storage,
            changes, wip.Count == 0);
    }

    private static string SerializeState(IEnumerable<Location> locations) => JsonSerializer.Serialize(
        locations.OrderBy(item => item.PalletNumber).Select(item => new
        {
            item.Id,
            item.Code,
            item.PalletNumber,
            item.OperationalRole,
            item.IsPhysicallyPresent,
            item.IsActive,
            item.IsBlocked,
            item.BlockReason
        }));

    private static LocationOperationalRole RackOperationalRole(IReadOnlyList<Location> locations) =>
        locations.Any(item => item.IsPhysicallyPresent) && locations.Where(item => item.IsPhysicallyPresent).All(item => item.OperationalRole == LocationOperationalRole.Wip)
            ? LocationOperationalRole.Wip
            : LocationOperationalRole.Storage;

    private static HashSet<short> RequestedWipPallets(LocationRackEditCommand command) =>
        command.WipPallets is null
            ? (command.OperationalRole == LocationOperationalRole.Wip ? command.PresentPallets.ToHashSet() : [])
            : command.WipPallets.ToHashSet();

    private static LocationRackEditSummary EmptySummary() => new([], [], [],
        LocationOperationalRole.Storage, LocationOperationalRole.Storage);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
