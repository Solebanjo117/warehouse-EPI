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

public sealed record LocationAreaDeletionState(Guid LocationId, string Code, bool CanDelete,
    IReadOnlyList<string> Blockers);

public sealed record LocationAreaDeleteCommand(Guid OperationId, Guid RequestedByUserId,
    Guid LocationId, string? Reason, string? Pin, string? ConfirmationCode);

public enum LocationAreaDeleteStatus
{
    Success,
    ValidationFailed,
    Unauthorized,
    InvalidPin,
    IdempotencyConflict,
    NotFound
}

public sealed record LocationAreaDeleteResult(LocationAreaDeleteStatus Status,
    IReadOnlyList<string>? Errors = null);

public sealed class LocationAreaAdministrationService(
    WarehouseDbContext dbContext,
    UserPinService pins,
    TimeProvider timeProvider)
{
    public async Task<LocationAreaDeletionState?> GetDeletionStateAsync(Guid locationId,
        CancellationToken token = default)
    {
        var location = await dbContext.Locations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == locationId && item.Kind == LocationKind.Area, token);
        if (location is null) return null;
        var blockers = await GetBlockersAsync(location.Id, token);
        return new(location.Id, location.Code, blockers.Count == 0, blockers);
    }

    public async Task<LocationAreaDeleteResult> DeleteAsync(LocationAreaDeleteCommand command,
        CancellationToken token = default)
    {
        var errors = ValidateCommand(command);
        if (errors.Count != 0) return new(LocationAreaDeleteStatus.ValidationFailed, errors);
        var requester = await dbContext.Users.AsNoTracking().Include(item => item.Role)
            .SingleOrDefaultAsync(item => item.Id == command.RequestedByUserId && item.IsActive &&
                item.Role.Code == "ADMIN", token);
        if (requester is null) return new(LocationAreaDeleteStatus.Unauthorized);
        var authorized = await pins.AuthenticateAsync(command.Pin ?? string.Empty, token);
        if (authorized is null || authorized.Role.Code != "ADMIN") return new(LocationAreaDeleteStatus.InvalidPin);

        var reason = command.Reason!.Trim();
        var confirmation = LocationNormalization.NormalizeCode(command.ConfirmationCode);
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            Action = "DELETE_AREA",
            command.RequestedByUserId,
            AuthorizedByUserId = authorized.Id,
            command.LocationId,
            Reason = reason,
            ConfirmationCode = confirmation
        }));

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, token)
            : null;
        if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            await dbContext.Locations.FromSqlInterpolated(
                $"SELECT * FROM locations WHERE id = {command.LocationId} FOR UPDATE").LoadAsync(token);
            await dbContext.WarehouseMapLayouts.FromSqlInterpolated(
                $"SELECT * FROM warehouse_map_layouts WHERE id = {1} FOR UPDATE").LoadAsync(token);
        }

        var existingRevision = await dbContext.WarehouseMapRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == command.OperationId, token);
        if (existingRevision is not null)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(existingRevision.RequestFingerprint == fingerprint
                ? LocationAreaDeleteStatus.Success
                : LocationAreaDeleteStatus.IdempotencyConflict);
        }

        var location = await dbContext.Locations.SingleOrDefaultAsync(item => item.Id == command.LocationId, token);
        if (location is null || location.Kind != LocationKind.Area)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationAreaDeleteStatus.NotFound);
        }
        if (!string.Equals(confirmation, location.Code, StringComparison.Ordinal))
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationAreaDeleteStatus.ValidationFailed,
                [$"Escribe {location.Code} para confirmar la eliminación definitiva."]);
        }

        var blockers = await GetBlockersAsync(location.Id, token);
        if (blockers.Count != 0)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationAreaDeleteStatus.ValidationFailed, blockers);
        }

        var now = timeProvider.GetUtcNow();
        var mapElements = await dbContext.WarehouseMapElements
            .Where(item => item.LocationId == location.Id).ToListAsync(token);
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
                    Action = "DELETE_AREA",
                    DeletedLocation = new
                    {
                        location.Id,
                        location.Code,
                        location.Description,
                        location.OperationalRole,
                        location.IsActive,
                        location.IsBlocked,
                        location.BlockReason
                    },
                    Removed = mapElements.Select(item => new
                    {
                        item.Id, item.LocationId, item.X, item.Y, item.Width, item.Height,
                        item.Rotation, item.ZIndex, item.IsVisible
                    })
                }),
                RequestedByUserId = requester.Id,
                AuthorizedByUserId = authorized.Id,
                RecordedAt = now
            });
        }

        dbContext.WarehouseMapElements.RemoveRange(mapElements);
        var processTargets = await dbContext.ProductionProcessWipTargets.Where(x => x.LocationId == location.Id).ToListAsync(token);
        if (processTargets.Count != 0)
        {
            dbContext.RemoveRange(processTargets);
            var configuration = await dbContext.ProductionProcessConfigurations.SingleOrDefaultAsync(x => x.Id == 1, token);
            if (configuration is not null) configuration.Version++;
        }
        dbContext.Locations.Remove(location);
        try
        {
            await dbContext.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationAreaDeleteStatus.ValidationFailed,
                ["El área cambió al mismo tiempo. Revisa nuevamente antes de eliminar."]);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationAreaDeleteStatus.ValidationFailed,
                ["El área recibió un registro relacionado y ya no puede eliminarse."]);
        }
        return new(LocationAreaDeleteStatus.Success);
    }

    private async Task<List<string>> GetBlockersAsync(Guid locationId, CancellationToken token)
    {
        var blockers = new List<string>();
        if (await dbContext.ProductLocationAssignments.AsNoTracking().AnyAsync(item => item.LocationId == locationId, token))
            blockers.Add("Tiene productos asignados, incluso si la asignación ya está inactiva.");
        if (await dbContext.InventoryBalances.AsNoTracking().AnyAsync(item => item.LocationId == locationId, token))
            blockers.Add("Tiene registros de existencias, incluso si el saldo actual es cero.");
        if (await dbContext.InventoryMovementLines.AsNoTracking().AnyAsync(item =>
                item.SourceLocationId == locationId || item.DestinationLocationId == locationId, token) ||
            await dbContext.InventoryMovements.AsNoTracking().AnyAsync(item => item.OperationalAreaId == locationId, token))
            blockers.Add("Tiene movimientos de inventario o actividad WIP.");
        if (await dbContext.InventoryBalanceChanges.AsNoTracking().AnyAsync(item => item.LocationId == locationId, token))
            blockers.Add("Tiene historial de cambios de saldo.");
        if (await dbContext.CycleCountLocations.AsNoTracking().AnyAsync(item => item.LocationId == locationId, token))
            blockers.Add("Fue incluida en uno o más conteos cíclicos.");
        if (await dbContext.OperationalExceptionCases.AsNoTracking().AnyAsync(item => item.LocationId == locationId, token))
            blockers.Add("Tiene incidencias operativas relacionadas.");
        if (await dbContext.WipDispositions.AsNoTracking().AnyAsync(item => item.DestinationLocationId == locationId, token))
            blockers.Add("Tiene devoluciones o disposiciones WIP relacionadas.");
        return blockers;
    }

    private static List<string> ValidateCommand(LocationAreaDeleteCommand command)
    {
        var errors = new List<string>();
        if (command.OperationId == Guid.Empty) errors.Add("La operación no es válida.");
        if (command.RequestedByUserId == Guid.Empty) errors.Add("La sesión ADMIN no es válida.");
        if (command.LocationId == Guid.Empty) errors.Add("El área no es válida.");
        var reason = command.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            errors.Add("Escribe un motivo de hasta 500 caracteres.");
        if (string.IsNullOrWhiteSpace(command.ConfirmationCode))
            errors.Add("Escribe el código del área para confirmar la eliminación definitiva.");
        return errors;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
