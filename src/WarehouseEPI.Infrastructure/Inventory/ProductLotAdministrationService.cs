using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Infrastructure.Inventory;

public sealed record ProductLotDateChangeCommand(Guid OperationId, Guid ProductLotId, Guid RequestedByUserId, string Pin, DateOnly? LotDate, string Reason);
public enum ProductLotDateChangeStatus { Success, InvalidPin, Unauthorized, ValidationFailed, IdempotencyConflict }
public sealed record ProductLotDateChangeResult(ProductLotDateChangeStatus Status, IReadOnlyList<string>? Errors = null)
{ public IReadOnlyList<string> ValidationErrors => Errors ?? []; }

public sealed class ProductLotAdministrationService(WarehouseDbContext dbContext, UserPinService pins, TimeProvider timeProvider)
{
    public async Task<ProductLotDateChangeResult> ChangeDateAsync(ProductLotDateChangeCommand command, CancellationToken cancellationToken = default)
    {
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (command.OperationId == Guid.Empty || command.ProductLotId == Guid.Empty || command.RequestedByUserId == Guid.Empty || reason.Length is 0 or > 500)
            return new(ProductLotDateChangeStatus.ValidationFailed, ["La operación, el lote y el motivo son obligatorios."]);
        var requested = await dbContext.Users.AsNoTracking().Include(item => item.Role).SingleOrDefaultAsync(item => item.Id == command.RequestedByUserId, cancellationToken);
        if (requested is null || !requested.IsActive || requested.Role.Code != "ADMIN") return new(ProductLotDateChangeStatus.Unauthorized);
        var authorized = await pins.AuthenticateAsync(command.Pin, cancellationToken);
        if (authorized is null || authorized.Role.Code != "ADMIN") return new(ProductLotDateChangeStatus.InvalidPin);
        var fingerprint = Hash($"{command.ProductLotId:N}|{command.RequestedByUserId:N}|{authorized.Id:N}|{command.LotDate}|{reason}");
        var existing = await dbContext.ProductLotDateChanges.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == command.OperationId, cancellationToken);
        if (existing is not null) return new(existing.RequestFingerprint == fingerprint ? ProductLotDateChangeStatus.Success : ProductLotDateChangeStatus.IdempotencyConflict);
        var lot = await dbContext.ProductLots.SingleOrDefaultAsync(item => item.Id == command.ProductLotId, cancellationToken);
        if (lot is null) return new(ProductLotDateChangeStatus.ValidationFailed, ["El lote no existe."]);
        await using var transaction = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var previous = lot.LotDate;
        lot.LotDate = command.LotDate;
        dbContext.ProductLotDateChanges.Add(new ProductLotDateChange { OperationId = command.OperationId, RequestFingerprint = fingerprint, ProductLotId = lot.Id, PreviousLotDate = previous, NewLotDate = command.LotDate, Reason = reason, RequestedByUserId = requested.Id, AuthorizedByUserId = authorized.Id, RecordedAt = timeProvider.GetUtcNow() });
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(ProductLotDateChangeStatus.Success);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
