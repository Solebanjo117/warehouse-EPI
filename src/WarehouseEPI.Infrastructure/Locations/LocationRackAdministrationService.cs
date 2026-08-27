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
    short RackNumber, IReadOnlyCollection<short> PresentPallets, string? Reason, string? Pin);

public sealed record LocationRackPositionState(Guid? Id, short PalletNumber, string Code,
    bool Exists, bool IsPhysicallyPresent, bool IsActive, bool IsBlocked, bool HasBalance,
    bool HasActiveAssignments);

public sealed record LocationRackEditView(string RowCode, short RackNumber,
    IReadOnlyList<LocationRackPositionState> Positions, IReadOnlyList<LocationRackRevisionView> Revisions);

public sealed record LocationRackRevisionView(Guid Id, string Reason, string RequestedBy,
    string AuthorizedBy, DateTimeOffset RecordedAt, string BeforeJson, string AfterJson);

public sealed record LocationRackEditSummary(IReadOnlyList<string> Added, IReadOnlyList<string> Restored,
    IReadOnlyList<string> Retired);

public sealed record LocationRackReviewResult(IReadOnlyList<string> Errors, LocationRackEditSummary Summary);

public enum LocationRackSaveStatus { Success, ValidationFailed, Unauthorized, InvalidPin, IdempotencyConflict, NotFound }

public sealed record LocationRackSaveResult(LocationRackSaveStatus Status,
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
        return new(row, rackNumber, states, revisions);
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
        errors.AddRange(await ValidateRetirementsAsync(locations, desired, token));
        var summary = BuildSummary(row, command.RackNumber, locations, desired);
        return new(errors.Distinct(StringComparer.Ordinal).ToArray(), summary);
    }

    public async Task<LocationRackSaveResult> SaveAsync(LocationRackEditCommand command,
        CancellationToken token = default)
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
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            command.RequestedByUserId,
            AuthorizedByUserId = authorized.Id,
            RowCode = row,
            command.RackNumber,
            PresentPallets = desired,
            Reason = reason
        }));

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
        errors = await ValidateRetirementsAsync(locations, desired.ToHashSet(), token);
        if (errors.Count != 0)
        {
            if (transaction is not null) await transaction.RollbackAsync(token);
            return new(LocationRackSaveStatus.ValidationFailed, errors);
        }

        var before = SerializeState(locations);
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
        }

        var after = SerializeState(locations.OrderBy(item => item.PalletNumber).ToArray());
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
                location is not null && withAssignments.Contains(location.Id));
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
        if (command.PresentPallets.Count is < 1 or > 9 || command.PresentPallets.Distinct().Count() != command.PresentPallets.Count ||
            command.PresentPallets.Any(item => item is < 1 or > 9))
            errors.Add("Selecciona entre una y nueve posiciones distintas.");
        var reason = command.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            errors.Add("Escribe un motivo de hasta 500 caracteres.");
        return errors;
    }

    private static LocationRackEditSummary BuildSummary(string row, short rack,
        IReadOnlyList<Location> locations, IReadOnlySet<short> desired)
    {
        var byPallet = locations.ToDictionary(item => item.PalletNumber!.Value);
        var added = desired.Where(item => !byPallet.ContainsKey(item)).Select(item => LocationNormalization.BuildRackCode(row, rack, item)).Order().ToArray();
        var restored = desired.Where(item => byPallet.TryGetValue(item, out var location) && !location.IsPhysicallyPresent).Select(item => byPallet[item].Code).Order().ToArray();
        var retired = locations.Where(item => item.IsPhysicallyPresent && !desired.Contains(item.PalletNumber!.Value)).Select(item => item.Code).Order().ToArray();
        return new(added, restored, retired);
    }

    private static string SerializeState(IEnumerable<Location> locations) => JsonSerializer.Serialize(
        locations.OrderBy(item => item.PalletNumber).Select(item => new
        {
            item.Id,
            item.Code,
            item.PalletNumber,
            item.IsPhysicallyPresent,
            item.IsActive,
            item.IsBlocked,
            item.BlockReason
        }));

    private static LocationRackEditSummary EmptySummary() => new([], [], []);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
